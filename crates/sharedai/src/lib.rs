//! A small, blocking client for the [SharedAI](https://github.com/tomblanchard312/SharedAI)
//! daemon: list its models and stream a chat reply.
//!
//! SharedAI runs models on the user's own machine. Its wire contract (docs/INFERENCE.md there) is
//! newline-delimited JSON over a private Unix socket: negotiate a protocol version, send one
//! request, read frames until exactly one terminal frame (`done`, `busy`, `inference_error`).
//!
//! This crate speaks that contract with `std` only (no async runtime), so the editor can run a
//! request on a plain thread. It is a clean implementation of the published contract, not a copy
//! of the SharedAI SDK. On targets without Unix sockets (Windows, wasm) every call returns
//! [`Error::Unsupported`].
#![deny(clippy::unwrap_used, clippy::expect_used, clippy::panic, clippy::unimplemented, clippy::todo, clippy::unreachable)]

use std::fmt;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use serde_json::{Value, json};

/// Protocol version for health and the model registry.
pub const VERSION_BASE: u64 = 1;
/// Protocol version that adds inference.
pub const VERSION_INFERENCE: u64 = 2;
/// Version 1 frames are at most 64 KiB; version 2 frames at most 4 MiB (both without the newline).
pub const MAX_FRAME_V1: usize = 64 << 10;
pub const MAX_FRAME_V2: usize = 4 << 20;
/// The daemon's caps: at most this many messages and `max_tokens`.
pub const MAX_MESSAGES: usize = 256;
pub const MAX_TOKENS: u32 = 4096;

/// How long to wait for the first event (it may include loading the model) and for each later one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Timeouts {
    pub first: Duration,
    pub next: Duration,
}
impl Default for Timeouts {
    fn default() -> Self {
        Self { first: Duration::from_secs(360), next: Duration::from_secs(60) }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Message {
    /// "system", "user" or "assistant".
    pub role: String,
    pub content: String,
}
impl Message {
    pub fn new(role: &str, content: impl Into<String>) -> Self {
        Self { role: role.into(), content: content.into() }
    }
}

/// Sampling controls; `None` takes the daemon's default.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Params {
    pub max_tokens: Option<u32>,
    pub temperature: Option<f32>,
    pub seed: Option<u32>,
}

/// How a finished stream ended, as the daemon reports it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Summary {
    /// `stop`, `length`, `stop_sequence`, `cancelled` or `time_limit`.
    pub reason: String,
    pub prompt_tokens: u64,
    pub completion_tokens: u64,
}

#[derive(Debug)]
pub enum Error {
    /// No socket path: neither the setting, `SHAREDAI_SOCKET` nor `$XDG_RUNTIME_DIR` gave one.
    NoEndpoint,
    /// This platform has no Unix sockets.
    Unsupported,
    Io(std::io::Error),
    /// A frame that isn't valid JSON, is too large, or isn't the expected reply.
    Protocol(String),
    /// The daemon predates inference.
    Incompatible,
    Busy {
        retry_after: Duration,
    },
    /// `kind` is the daemon's error kind (`model_not_found`, `context_overflow`, …).
    Inference {
        kind: String,
        message: String,
    },
    Timeout,
    Cancelled,
    /// A request outside the daemon's limits, refused before anything was sent.
    Invalid(String),
}
impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NoEndpoint => write!(f, "SharedAI isn't configured: set SHAREDAI_SOCKET or the socket path under Connection"),
            Self::Unsupported => write!(f, "SharedAI isn't available on this platform"),
            Self::Io(e) if e.kind() == std::io::ErrorKind::NotFound || e.kind() == std::io::ErrorKind::ConnectionRefused => {
                write!(f, "SharedAI isn't running ({e})")
            }
            Self::Io(e) => write!(f, "SharedAI connection: {e}"),
            Self::Protocol(m) => write!(f, "SharedAI sent an unexpected reply: {m}"),
            Self::Incompatible => write!(f, "This SharedAI daemon has no local inference; update it"),
            Self::Busy { retry_after } => write!(f, "SharedAI is busy; try again in {} s", retry_after.as_secs().max(1)),
            Self::Inference { kind, message } => write!(f, "SharedAI ({kind}): {message}"),
            Self::Timeout => write!(f, "SharedAI stopped responding"),
            Self::Cancelled => write!(f, "Stopped"),
            Self::Invalid(m) => write!(f, "{m}"),
        }
    }
}
impl std::error::Error for Error {}
impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

/// The socket to use: `configured` if non-empty, else `$SHAREDAI_SOCKET`, else
/// `$XDG_RUNTIME_DIR/sharedai/ipc.sock` (the Nisaba desktop's convention).
pub fn endpoint(configured: &str) -> Result<PathBuf, Error> {
    let configured = configured.trim();
    if !configured.is_empty() {
        return Ok(PathBuf::from(configured));
    }
    if let Some(p) = std::env::var_os("SHAREDAI_SOCKET").filter(|p| !p.is_empty()) {
        return Ok(PathBuf::from(p));
    }
    match std::env::var_os("XDG_RUNTIME_DIR").filter(|p| !p.is_empty()) {
        Some(dir) => Ok(PathBuf::from(dir).join("sharedai").join("ipc.sock")),
        None => Err(Error::NoEndpoint),
    }
}

/// A newline-terminated JSON frame for `request`, checked against `limit`.
pub fn encode(request: &Value, limit: usize) -> Result<Vec<u8>, Error> {
    let mut data = serde_json::to_vec(request).map_err(|e| Error::Protocol(e.to_string()))?;
    if data.len() > limit {
        return Err(Error::Invalid(format!("The request is larger than SharedAI accepts ({} KiB)", limit >> 10)));
    }
    data.push(b'\n');
    Ok(data)
}

/// The chat request frame (protocol v2), after checking the daemon's limits.
pub fn chat_request(model: &str, messages: &[Message], params: &Params) -> Result<Value, Error> {
    if model.trim().is_empty() {
        return Err(Error::Invalid("Choose a SharedAI model first".into()));
    }
    if messages.is_empty() || messages.len() > MAX_MESSAGES {
        return Err(Error::Invalid(format!("A chat has 1 to {MAX_MESSAGES} messages")));
    }
    let mut p = serde_json::Map::new();
    if let Some(n) = params.max_tokens {
        p.insert("max_tokens".into(), json!(n.clamp(1, MAX_TOKENS)));
    }
    if let Some(t) = params.temperature.filter(|t| t.is_finite()) {
        p.insert("temperature".into(), json!(t.clamp(0.0, 2.0)));
    }
    if let Some(s) = params.seed {
        p.insert("seed".into(), json!(s));
    }
    let messages: Vec<Value> = messages.iter().map(|m| json!({"role": m.role, "content": m.content})).collect();
    Ok(json!({"type": "chat", "model": model, "messages": messages, "params": Value::Object(p)}))
}

/// One parsed stream event.
#[derive(Clone, Debug, PartialEq)]
pub enum Event {
    Chunk { seq: u64, text: String },
    Done(Summary),
}

/// Interpret one frame of a v2 stream. Terminal errors come back as `Err`.
pub fn parse_event(frame: &[u8]) -> Result<Event, Error> {
    let v: Value = serde_json::from_slice(frame).map_err(|e| Error::Protocol(e.to_string()))?;
    let str_of = |k: &str| v.get(k).and_then(Value::as_str).unwrap_or("").to_string();
    let num_of = |k: &str| v.get(k).and_then(Value::as_u64).unwrap_or(0);
    match v.get("type").and_then(Value::as_str) {
        Some("chunk") => Ok(Event::Chunk { seq: num_of("seq"), text: str_of("text") }),
        Some("done") => Ok(Event::Done(Summary {
            reason: str_of("reason"),
            prompt_tokens: num_of("prompt_tokens"),
            completion_tokens: num_of("completion_tokens"),
        })),
        Some("busy") => Err(Error::Busy { retry_after: Duration::from_millis(num_of("retry_after_ms")) }),
        Some("inference_error") => Err(Error::Inference { kind: str_of("kind"), message: str_of("message") }),
        Some("incompatible") => Err(Error::Incompatible),
        Some("error") => Err(Error::Protocol(str_of("message"))),
        other => Err(Error::Protocol(format!("frame type {other:?}"))),
    }
}

/// Reads bounded newline-delimited frames, keeping partial input across read timeouts.
pub struct FrameReader<R> {
    inner: R,
    buf: Vec<u8>,
    limit: usize,
}
impl<R: std::io::Read> FrameReader<R> {
    pub fn new(inner: R, limit: usize) -> Self {
        Self { inner, buf: Vec::new(), limit }
    }

    /// The next frame; `Ok(None)` at a clean end of stream. A read timeout surfaces as the
    /// `WouldBlock`/`TimedOut` I/O error with buffered bytes kept for the next call.
    pub fn next_frame(&mut self) -> Result<Option<Vec<u8>>, Error> {
        loop {
            if let Some(i) = self.buf.iter().position(|b| *b == b'\n') {
                let rest = self.buf.split_off(i + 1);
                let mut frame = std::mem::replace(&mut self.buf, rest);
                frame.pop();
                return Ok(Some(frame));
            }
            if self.buf.len() > self.limit {
                return Err(Error::Protocol("frame too large".into()));
            }
            let mut chunk = [0u8; 8192];
            let n = self.inner.read(&mut chunk)?;
            if n == 0 {
                return if self.buf.is_empty() { Ok(None) } else { Err(Error::Protocol("truncated frame".into())) };
            }
            self.buf.extend_from_slice(chunk.get(..n).unwrap_or(&[]));
        }
    }
}

fn is_timeout(e: &Error) -> bool {
    matches!(e, Error::Io(io) if matches!(io.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut))
}

/// Drive one stream to its terminal frame: `on_chunk` gets each piece of text in order. Polls
/// `cancel` between reads; on cancel it sends `cancel` and returns [`Error::Cancelled`].
pub fn drive<S: std::io::Read>(
    reader: &mut FrameReader<S>,
    cancel: &AtomicBool,
    timeouts: Timeouts,
    mut write_cancel: impl FnMut(&[u8]) -> std::io::Result<()>,
    mut on_chunk: impl FnMut(&str),
) -> Result<Summary, Error> {
    let mut expect_seq = 0u64;
    let mut waited = Duration::ZERO;
    let mut limit = timeouts.first;
    loop {
        if cancel.load(Ordering::Relaxed) {
            let _ = write_cancel(b"{\"type\":\"cancel\"}\n");
            return Err(Error::Cancelled);
        }
        match reader.next_frame() {
            Ok(Some(frame)) => {
                waited = Duration::ZERO;
                limit = timeouts.next;
                match parse_event(&frame)? {
                    Event::Chunk { seq, text } => {
                        if seq != expect_seq {
                            return Err(Error::Protocol(format!("chunk {seq} out of order, expected {expect_seq}")));
                        }
                        expect_seq = expect_seq.saturating_add(1);
                        on_chunk(&text);
                    }
                    Event::Done(summary) => return Ok(summary),
                }
            }
            Ok(None) => return Err(Error::Protocol("the stream ended without a result".into())),
            Err(e) if is_timeout(&e) => {
                waited = waited.saturating_add(POLL);
                if waited >= limit {
                    return Err(Error::Timeout);
                }
            }
            Err(e) => return Err(e),
        }
    }
}

/// How often a waiting stream checks for cancellation.
pub const POLL: Duration = Duration::from_millis(200);

#[cfg(unix)]
mod net {
    use super::*;
    use std::io::Write;
    use std::os::unix::net::UnixStream;
    use std::path::Path;

    fn connect(endpoint: &Path, app: &str, version: u64) -> Result<FrameReader<UnixStream>, Error> {
        let mut stream = UnixStream::connect(endpoint)?;
        stream.set_read_timeout(Some(Duration::from_secs(5)))?;
        stream.set_write_timeout(Some(Duration::from_secs(30)))?;
        stream.write_all(&encode(&json!({"type": "negotiate", "versions": [version], "app": app}), MAX_FRAME_V1)?)?;
        let limit = if version >= VERSION_INFERENCE { MAX_FRAME_V2 } else { MAX_FRAME_V1 };
        let mut reader = FrameReader::new(stream, limit);
        let frame = reader.next_frame()?.ok_or_else(|| Error::Protocol("no reply to negotiate".into()))?;
        let v: Value = serde_json::from_slice(&frame).map_err(|e| Error::Protocol(e.to_string()))?;
        match (v.get("type").and_then(Value::as_str), v.get("version").and_then(Value::as_u64)) {
            (Some("negotiated"), Some(got)) if got == version => Ok(reader),
            (Some("negotiated" | "incompatible"), _) => Err(Error::Incompatible),
            // Daemons from before negotiation answer "Invalid request"; they have no inference.
            (Some("error"), _) if version >= VERSION_INFERENCE => Err(Error::Incompatible),
            _ => Err(Error::Protocol(format!("negotiate reply {v}"))),
        }
    }

    pub fn list_models(endpoint: &Path, app: &str) -> Result<Vec<String>, Error> {
        let mut reader = connect(endpoint, app, VERSION_BASE)?;
        reader.inner.write_all(&encode(&json!({"type": "list_models"}), MAX_FRAME_V1)?)?;
        let frame = reader.next_frame()?.ok_or_else(|| Error::Protocol("no model list".into()))?;
        let v: Value = serde_json::from_slice(&frame).map_err(|e| Error::Protocol(e.to_string()))?;
        match v.get("ids").and_then(Value::as_array) {
            Some(ids) if v.get("type").and_then(Value::as_str) == Some("models") => {
                Ok(ids.iter().filter_map(Value::as_str).map(str::to_string).collect())
            }
            _ => Err(Error::Protocol(format!("list_models reply {v}"))),
        }
    }

    pub fn chat(
        endpoint: &Path,
        app: &str,
        model: &str,
        messages: &[Message],
        params: &Params,
        timeouts: Timeouts,
        cancel: &AtomicBool,
        on_chunk: impl FnMut(&str),
    ) -> Result<Summary, Error> {
        let request = encode(&chat_request(model, messages, params)?, MAX_FRAME_V2)?;
        let mut reader = connect(endpoint, app, VERSION_INFERENCE)?;
        reader.inner.write_all(&request)?;
        reader.inner.set_read_timeout(Some(POLL))?;
        let mut writer = reader.inner.try_clone()?;
        drive(&mut reader, cancel, timeouts, |b| writer.write_all(b), on_chunk)
    }
}

/// The ids of the models registered with the daemon.
pub fn list_models(endpoint: &std::path::Path, app: &str) -> Result<Vec<String>, Error> {
    #[cfg(unix)]
    return net::list_models(endpoint, app);
    #[cfg(not(unix))]
    {
        let _ = (endpoint, app);
        Err(Error::Unsupported)
    }
}

/// Stream a chat reply from `model`, formatted with the model's own chat template. Blocks until
/// the reply ends; run it on a worker thread.
#[allow(clippy::too_many_arguments)]
pub fn chat(
    endpoint: &std::path::Path,
    app: &str,
    model: &str,
    messages: &[Message],
    params: &Params,
    timeouts: Timeouts,
    cancel: &AtomicBool,
    on_chunk: impl FnMut(&str),
) -> Result<Summary, Error> {
    #[cfg(unix)]
    return net::chat(endpoint, app, model, messages, params, timeouts, cancel, on_chunk);
    #[cfg(not(unix))]
    {
        let _ = (endpoint, app, model, messages, params, timeouts, cancel, on_chunk);
        Err(Error::Unsupported)
    }
}

#[cfg(test)]
mod tests;
