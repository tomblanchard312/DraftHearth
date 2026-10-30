use sharedai_protocol::{MAX_FRAME_BYTES, Request, Response, VERSION, read_frame};
use std::{
    fmt, io,
    path::{Path, PathBuf},
    time::Duration,
};
use tokio::{
    io::{AsyncRead, AsyncWrite, AsyncWriteExt, BufReader},
    sync::watch,
    time::{sleep, timeout},
};

mod download;
mod inference;
mod service;
pub use download::{DownloadReport, default_image_model_present, download_default_image_model, download_image_model, image_model_present};
pub use inference::{Generation, ImageRequest, InferenceTimeouts, Picture, Summary};
pub use service::Service;
pub use sharedai_protocol::{
    CPU_IMAGE_MODEL_ID, ChatMessage, DEFAULT_IMAGE_CFG, DEFAULT_IMAGE_MODEL_ID, DEFAULT_IMAGE_SIDE, DEFAULT_IMAGE_STEPS, GPU_IMAGE_MODEL_ID,
    GenerateParams, ImageProfile, ImageTask, InferenceErrorKind, StopReason, image_profile, limits,
};

/// The socket the daemon uses by default: `SHAREDAI_SOCKET`, else
/// `$XDG_RUNTIME_DIR/sharedai/ipc.sock`.
#[cfg(unix)]
pub fn default_endpoint() -> Option<PathBuf> {
    sharedai_protocol::unix_endpoint().map(|(path, _)| path)
}

/// The endpoint the per-user daemon listens on, on this platform.
pub fn service_endpoint() -> Option<PathBuf> {
    #[cfg(unix)]
    {
        default_endpoint()
    }
    #[cfg(windows)]
    {
        std::env::var_os("SHAREDAI_PIPE")
            .map(PathBuf::from)
            .filter(|p| !p.as_os_str().is_empty())
            .or_else(|| sharedai_protocol::windows::pipe_path("default").ok())
    }
}

/// Load a diffusion model and leave it resident. Draws nothing.
/// The daemon must already be running. A model that is already loaded only has its idle timer refreshed.
pub fn prepare_image(app: &str, model: &str) -> Result<(), Error> {
    let endpoint = service_endpoint()
        .ok_or_else(|| Error::Remote("SharedAI has no endpoint. Set XDG_RUNTIME_DIR or SHAREDAI_SOCKET, and start sharedai-daemon.".into()))?;
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async { Client::new(endpoint, app).prepare(model).await })
}

/// Draw one image on the per-user daemon. The call blocks until the PNG is ready.
/// The daemon must already be running.
pub fn generate_image(app: &str, request: ImageRequest) -> Result<Picture, Error> {
    generate_image_with_progress(app, request, |_, _| {})
}

/// [`generate_image`], calling `on_step` with each sampling step (`step`, `total`).
/// Nothing is reported while the model is still loading.
pub fn generate_image_with_progress(app: &str, request: ImageRequest, on_step: impl FnMut(u32, u32) + Send) -> Result<Picture, Error> {
    generate_image_cancellable(app, request, &Cancellation::new(), on_step)
}

pub fn generate_image_cancellable(
    app: &str,
    request: ImageRequest,
    cancellation: &Cancellation,
    mut on_step: impl FnMut(u32, u32) + Send,
) -> Result<Picture, Error> {
    let endpoint = service_endpoint()
        .ok_or_else(|| Error::Remote("SharedAI has no endpoint. Set XDG_RUNTIME_DIR or SHAREDAI_SOCKET, and start sharedai-daemon.".into()))?;
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async { Client::new(endpoint, app).imagine_with(request, cancellation, &mut on_step).await })
}

/// Ask Enki for its hardware recommendation without loading weights.
pub fn recommended_image_profile(app: &str) -> Result<ImageProfile, Error> {
    request_image_profile(app, None)
}

/// Ask for the current CPU or GPU recommendation from the installed daemon catalog.
pub fn recommended_image_profile_for(app: &str, cpu: bool) -> Result<ImageProfile, Error> {
    request_image_profile(app, Some(cpu))
}

fn request_image_profile(app: &str, cpu: Option<bool>) -> Result<ImageProfile, Error> {
    let endpoint = service_endpoint().ok_or_else(|| Error::Remote("Enki has no endpoint".into()))?;
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async {
        match Client::new(endpoint, app).request(Request::ImageDefaults { cpu }, &Cancellation::new()).await? {
            Response::ImageDefaults { profile } => Ok(profile),
            other => Err(Error::Remote(format!("Enki cannot recommend an image model: {other:?}"))),
        }
    })
}

/// Diffusion model ids registered with the daemon, in alphabetical order.
/// Language models are left out. The daemon must already be running.
pub fn diffusion_models(app: &str) -> Result<Vec<String>, Error> {
    let endpoint = service_endpoint()
        .ok_or_else(|| Error::Remote("SharedAI has no endpoint. Set XDG_RUNTIME_DIR or SHAREDAI_SOCKET, and start sharedai-daemon.".into()))?;
    let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async {
        let client = Client::new(endpoint, app);
        let cancellation = Cancellation::new();
        let Response::Models { ids } = client.request(Request::ListModels, &cancellation).await? else {
            return Err(Error::UnexpectedResponse);
        };
        let mut found = Vec::new();
        for id in ids {
            if let Response::Model { model: Some(model) } = client.request(Request::GetModel { id }, &cancellation).await?
                && sharedai_protocol::is_diffusion_model(&model.path)
            {
                found.push(model.id);
            }
        }
        found.sort();
        Ok(found)
    })
}

#[derive(Debug)]
pub enum Error {
    Io(io::Error),
    InvalidFrame(serde_json::Error),
    TooLarge,
    Timeout,
    Cancelled,
    Incompatible {
        supported: Vec<u32>,
    },
    UnexpectedResponse,
    Remote(String),
    Registry(sharedai_protocol::RegistryErrorKind),
    ServiceExited,
    WrongInstance,
    /// The daemon's queue is full; nothing was started.
    Busy {
        retry_after: Duration,
    },
    /// Inference failed; `kind` says why.
    Inference {
        kind: InferenceErrorKind,
        message: String,
    },
}
impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(err) => write!(f, "Local transport: {err}"),
            Self::InvalidFrame(err) => write!(f, "Invalid response: {err}"),
            Self::TooLarge => write!(f, "Request exceeds {MAX_FRAME_BYTES} bytes"),
            Self::Timeout => write!(f, "SharedAI deadline exceeded"),
            Self::Cancelled => write!(f, "SharedAI request cancelled"),
            Self::Incompatible { supported } => {
                write!(f, "Incompatible protocol response {supported:?}. Check the offered versions or update the SDK or daemon")
            }
            Self::UnexpectedResponse => write!(f, "Unexpected SharedAI response"),
            Self::Registry(kind) => write!(f, "Model registry: {kind:?}"),
            Self::Remote(message) => write!(f, "SharedAI: {message}"),
            Self::ServiceExited => write!(f, "SharedAI process exited before becoming ready"),
            Self::WrongInstance => write!(f, "Endpoint belongs to another daemon; use its client or choose a separate endpoint"),
            Self::Busy { retry_after } => write!(f, "SharedAI is busy; retry in {retry_after:?}"),
            Self::Inference { kind, message } => {
                write!(f, "SharedAI inference ({kind:?}): {message}")
            }
        }
    }
}
impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(err) => Some(err),
            Self::InvalidFrame(err) => Some(err),
            _ => None,
        }
    }
}
impl From<io::Error> for Error {
    fn from(err: io::Error) -> Self {
        Self::Io(err)
    }
}
impl From<serde_json::Error> for Error {
    fn from(err: serde_json::Error) -> Self {
        Self::InvalidFrame(err)
    }
}

#[derive(Clone, Debug)]
pub struct Cancellation(watch::Sender<bool>);
impl Default for Cancellation {
    fn default() -> Self {
        Self::new()
    }
}
impl Cancellation {
    pub fn new() -> Self {
        Self(watch::channel(false).0)
    }
    pub fn cancel(&self) {
        self.0.send_replace(true);
    }
    pub async fn cancelled(&self) {
        let mut receiver = self.0.subscribe();
        let _ = receiver.wait_for(|value| *value).await;
    }
}

trait Stream: AsyncRead + AsyncWrite + Unpin + Send {}
impl<T: AsyncRead + AsyncWrite + Unpin + Send> Stream for T {}
type Connection = BufReader<Box<dyn Stream>>;

#[derive(Clone, Debug)]
pub struct Client {
    endpoint: PathBuf,
    app: String,
    inference_timeouts: InferenceTimeouts,
}
impl Client {
    pub fn new(endpoint: impl Into<PathBuf>, app: impl Into<String>) -> Self {
        Self { endpoint: endpoint.into(), app: app.into(), inference_timeouts: InferenceTimeouts::default() }
    }

    /// One connection per operation. Retries stop before any operation is written.
    /// Dropping this future or cancelling closes its connection; no operation is replayed.
    pub async fn request(&self, request: Request, cancellation: &Cancellation) -> Result<Response, Error> {
        let data = encode(&request)?;
        bounded(cancellation, async {
            let (mut stream, _) = self.connect().await?;
            stream.write_all(&data).await?;
            let response = receive(&mut stream).await?;
            match (&request, &response) {
                (_, Response::RegistryError { kind }) => Err(Error::Registry(*kind)),
                (_, Response::Error { message }) => Err(Error::Remote(message.clone())),
                (_, Response::Incompatible { supported }) => Err(Error::Incompatible { supported: supported.clone() }),
                (Request::Health, Response::Health { .. })
                | (Request::ListModels, Response::Models { .. })
                | (Request::ImageDefaults { .. }, Response::ImageDefaults { .. })
                | (Request::ImageCatalog { .. }, Response::ImageCatalog { .. })
                | (Request::RegisterModels { .. }, Response::Registered)
                | (Request::GetModel { .. }, Response::Model { .. })
                | (Request::RemoveModel { .. }, Response::Removed { .. })
                | (Request::ReleaseModel { .. }, Response::ModelReleased { .. } | Response::Busy { .. })
                | (Request::Shutdown, Response::Stopping) => Ok(response),
                (Request::Hello { version: requested, .. }, Response::Hello { version }) if requested == version => Ok(response),
                (Request::Negotiate { versions, .. }, Response::Negotiated { version, .. }) if versions.contains(version) => Ok(response),
                (Request::Hello { .. }, Response::Hello { version }) | (Request::Negotiate { .. }, Response::Negotiated { version, .. }) => {
                    Err(Error::Incompatible { supported: vec![*version] })
                }
                _ => Err(Error::UnexpectedResponse),
            }
        })
        .await
    }

    async fn connect(&self) -> Result<(Connection, Option<u32>), Error> {
        let negotiation = encode(&Request::Negotiate { versions: vec![VERSION], app: self.app.clone() })?;
        for attempt in 0..=4 {
            let result = async {
                let mut stream = BufReader::new(open(&self.endpoint).await?);
                stream.write_all(&negotiation).await?;
                let response = receive(&mut stream).await?;
                match response {
                    Response::Negotiated { version, process_id } if version == VERSION => Ok((stream, Some(process_id))),
                    Response::Incompatible { supported } => Err(Error::Incompatible { supported }),
                    // PR1 daemons understand Hello but not Negotiate. Never downgrade on a transport error.
                    Response::Error { message } if message == "Invalid request" => {
                        stream.write_all(&encode(&Request::Hello { version: VERSION, app: self.app.clone() })?).await?;
                        match receive(&mut stream).await? {
                            Response::Hello { version } if version == VERSION => Ok((stream, None)),
                            _ => Err(Error::Incompatible { supported: vec![] }),
                        }
                    }
                    Response::Negotiated { version, .. } => Err(Error::Incompatible { supported: vec![version] }),
                    Response::Error { message } => Err(Error::Remote(message)),
                    _ => Err(Error::UnexpectedResponse),
                }
            }
            .await;
            match result {
                Err(Error::Io(ref err)) if attempt < 4 && retryable(err) => sleep(Duration::from_millis(25 << attempt)).await,
                other => return other,
            }
        }
        unreachable!()
    }
}

async fn bounded<T>(cancellation: &Cancellation, future: impl std::future::Future<Output = Result<T, Error>>) -> Result<T, Error> {
    tokio::select! {
        biased;
        _ = cancellation.cancelled() => Err(Error::Cancelled),
        result = timeout(Duration::from_secs(5), future) => result.map_err(|_| Error::Timeout)?,
    }
}
fn encode(request: &Request) -> Result<Vec<u8>, Error> {
    let mut data = serde_json::to_vec(request)?;
    if data.len() > MAX_FRAME_BYTES {
        return Err(Error::TooLarge);
    }
    data.push(b'\n');
    Ok(data)
}
async fn receive(stream: &mut Connection) -> Result<Response, Error> {
    let frame = read_frame(stream).await?.ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "Missing response"))?;
    Ok(serde_json::from_slice(&frame)?)
}
fn retryable(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::NotFound
            | io::ErrorKind::ConnectionRefused
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::UnexpectedEof
            | io::ErrorKind::WouldBlock
    ) || cfg!(windows) && error.raw_os_error() == Some(231) // ERROR_PIPE_BUSY
}

#[cfg(unix)]
async fn open(path: &Path) -> io::Result<Box<dyn Stream>> {
    let stream = tokio::net::UnixStream::connect(path).await?;
    // geteuid has no preconditions and does not mutate process state.
    if stream.peer_cred()?.uid() != unsafe { libc::geteuid() } {
        return Err(io::Error::new(io::ErrorKind::PermissionDenied, "Unexpected daemon identity"));
    }
    Ok(Box::new(stream))
}
#[cfg(windows)]
async fn open(path: &Path) -> io::Result<Box<dyn Stream>> {
    sharedai_protocol::windows::validate_path(path)?;
    // Tokio defaults to identification-only QoS, preventing server impersonation of this client.
    let stream = tokio::net::windows::named_pipe::ClientOptions::new().open(path)?;
    sharedai_protocol::windows::verify_peer(&stream, true)?;
    Ok(Box::new(stream))
}

/// Compatibility entry point; use Client::request to supply cancellation and app identity.
pub async fn request(path: &Path, request: Request) -> Result<Response, Error> {
    Client::new(path, "sharedai-client").request(request, &Cancellation::new()).await
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn cancellation_and_retry_policy() {
        let cancellation = Cancellation::new();
        cancellation.cancel();
        assert!(matches!(bounded(&cancellation, async { Ok(()) }).await, Err(Error::Cancelled)));
        let cancellation = Cancellation::new();
        let clone = cancellation.clone();
        tokio::spawn(async move {
            clone.cancel();
        });
        assert!(matches!(bounded(&cancellation, std::future::pending::<Result<(), Error>>()).await, Err(Error::Cancelled)));
        assert!(retryable(&io::Error::from(io::ErrorKind::NotFound)));
        assert!(!retryable(&io::Error::from(io::ErrorKind::PermissionDenied)));
        assert!(!retryable(&io::Error::from(io::ErrorKind::InvalidData)));
        assert!(matches!(encode(&Request::Hello { version: 1, app: "x".repeat(MAX_FRAME_BYTES) }), Err(Error::TooLarge)));
    }
}
