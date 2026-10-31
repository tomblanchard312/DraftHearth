use super::*;
use tokio::process::{Child, Command};

/// An explicitly launched ordinary-user daemon. Drop kills only this owned child.
/// This does not install an OS service, modify startup settings, or attach to another daemon.
pub struct Service {
    child: Child,
    client: Client,
}
impl Service {
    pub async fn start(executable: &Path, endpoint: &Path, cancellation: &Cancellation) -> Result<Self, Error> {
        Self::launch(executable, endpoint, None, cancellation, true).await
    }

    /// Use an explicitly selected database in an existing private data directory.
    pub async fn start_with_registry(executable: &Path, endpoint: &Path, registry: &Path, cancellation: &Cancellation) -> Result<Self, Error> {
        Self::launch(executable, endpoint, Some(registry), cancellation, true).await
    }

    async fn launch(
        executable: &Path,
        endpoint: &Path,
        registry: Option<&Path>,
        cancellation: &Cancellation,
        kill_on_drop: bool,
    ) -> Result<Self, Error> {
        if *cancellation.0.borrow() {
            return Err(Error::Cancelled);
        }
        let mut command = Command::new(executable);
        command.kill_on_drop(kill_on_drop).stdin(std::process::Stdio::null()).stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null());
        #[cfg(unix)]
        command.env("SHAREDAI_SOCKET", endpoint);
        #[cfg(windows)]
        {
            command.env("SHAREDAI_PIPE", endpoint);
            // CREATE_NO_WINDOW: a library caller does not need a console or elevation prompt.
            command.creation_flags(0x08000000);
        }
        if let Some(directory) = executable.parent() {
            let whisper = directory.join(if cfg!(windows) { "whisper-cli.exe" } else { "whisper-cli" });
            if whisper.is_file() && std::env::var_os("ENKI_WHISPER_CLI").is_none() {
                command.env("ENKI_WHISPER_CLI", whisper);
            }
        }
        if let Some(registry) = registry {
            command.env("SHAREDAI_REGISTRY", registry);
        }
        let mut child = command.spawn()?;
        let pid = child.id().ok_or(Error::ServiceExited)?;
        let client = Client::new(endpoint, "sharedai-lifecycle");
        let ready = bounded(cancellation, async {
            tokio::select! {
                biased;
                status = child.wait() => { status?; Err(Error::ServiceExited) },
                connected = async {
                    loop {
                        match client.connect().await {
                            Err(Error::Io(err)) if retryable(&err) => sleep(Duration::from_millis(25)).await,
                            other => break other,
                        }
                    }
                } => {
                    let (mut stream, owner) = connected?;
                    if owner != Some(pid) { return Err(Error::WrongInstance); }
                    stream.write_all(&encode(&Request::Health)?).await?;
                    match receive(&mut stream).await? {
                        Response::Health { status } if status == "ok" => Ok(()),
                        _ => Err(Error::UnexpectedResponse),
                    }
                }
            }
        })
        .await;
        if let Err(error) = ready {
            let _ = child.kill().await;
            let _ = child.wait().await;
            return Err(error);
        }
        Ok(Self { child, client })
    }
    pub fn client(&self) -> &Client {
        &self.client
    }

    pub async fn stop(mut self, cancellation: &Cancellation) -> Result<(), Error> {
        // Verify ownership on the same connection used for shutdown: a replacement daemon is never stopped.
        let result = bounded(cancellation, async {
            let (mut stream, owner) = self.client.connect().await?;
            if owner != self.child.id() || owner.is_none() {
                return Err(Error::WrongInstance);
            }
            stream.write_all(&encode(&Request::Shutdown)?).await?;
            if !matches!(receive(&mut stream).await?, Response::Stopping) {
                return Err(Error::UnexpectedResponse);
            }
            if !self.child.wait().await?.success() {
                return Err(Error::ServiceExited);
            }
            Ok(())
        })
        .await;
        if result.is_err() {
            let _ = self.child.kill().await;
            let _ = self.child.wait().await;
        }
        result
    }
}

/// Locate only app-relative runtime directories; never search the working directory or PATH.
pub fn bundled_daemon(executable: &Path, app: &str) -> Option<PathBuf> {
    if app.is_empty() || !app.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-') {
        return None;
    }
    let parent = executable.parent()?;
    let name = if cfg!(windows) { "sharedai-daemon.exe" } else { "sharedai-daemon" };
    [parent.join("enki").join(name), parent.join("../lib").join(app).join("enki").join(name)].into_iter().find(|path| path.is_file())
}

impl Client {
    /// Reuse a healthy service, otherwise launch only the app's bundled runtime.
    pub async fn ensure_bundled(&self, executable: &Path, app: &str, cancellation: &Cancellation) -> Result<(), Error> {
        match self.request(Request::Health, cancellation).await {
            Ok(Response::Health { status }) if status == "ok" => return Ok(()),
            Err(Error::Io(error)) if matches!(error.kind(), io::ErrorKind::NotFound | io::ErrorKind::ConnectionRefused) => {}
            Err(error) => return Err(error),
            _ => return Err(Error::UnexpectedResponse),
        }
        let daemon =
            bundled_daemon(executable, app).ok_or_else(|| Error::Io(io::Error::new(io::ErrorKind::NotFound, "Bundled Enki runtime is missing")))?;
        match Service::launch(&daemon, &self.endpoint, None, cancellation, false).await {
            Ok(service) => {
                drop(service);
                Ok(())
            }
            Err(error) => {
                // Another app may have won the ordinary-user service startup race.
                match self.request(Request::Health, cancellation).await {
                    Ok(Response::Health { status }) if status == "ok" => Ok(()),
                    _ => Err(error),
                }
            }
        }
    }
}

#[cfg(test)]
mod bundle_tests {
    use super::*;
    #[test]
    fn bundles_are_app_relative_and_reject_path_components() {
        let root = std::env::temp_dir().join(format!("enki-bundle-path-{}", std::process::id()));
        let runtime = root.join("bin/enki");
        std::fs::create_dir_all(&runtime).unwrap();
        let name = if cfg!(windows) { "sharedai-daemon.exe" } else { "sharedai-daemon" };
        std::fs::write(runtime.join(name), b"fixture").unwrap();
        let executable = root.join("bin/app");
        assert_eq!(bundled_daemon(&executable, "inanna"), Some(runtime.join(name)));
        for app in ["", "../inanna", "inanna/x", "/inanna"] {
            assert!(bundled_daemon(&executable, app).is_none());
        }
        std::fs::remove_dir_all(root).unwrap();
    }
}
