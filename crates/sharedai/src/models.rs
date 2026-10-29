//! On-demand, checksum-pinned GGUF downloads, following FileExplorer's model installation flow.
use crate::Error;
use std::path::Path;
use std::sync::atomic::AtomicBool;
#[cfg(any(not(target_arch = "wasm32"), test))]
use std::sync::atomic::Ordering;

pub struct Model {
    pub id: &'static str,
    pub name: &'static str,
    pub repo: &'static str,
    pub revision: &'static str,
    pub file: &'static str,
    pub size: u64,
    pub sha256: &'static str,
    pub license: &'static str,
}

/// Exact repository revisions and LFS hashes reviewed on 2026-10-25.
pub const CATALOG: &[Model] = &[
    Model {
        id: "qwen3-4b",
        name: "Qwen3 4B",
        repo: "Qwen/Qwen3-4B-GGUF",
        revision: "bc640142c66e1fdd12af0bd68f40445458f3869b",
        file: "Qwen3-4B-Q4_K_M.gguf",
        size: 2497280256,
        sha256: "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
        license: "Apache-2.0",
    },
    Model {
        id: "gemma3-4b",
        name: "Gemma 3 4B",
        repo: "google/gemma-3-4b-it-qat-q4_0-gguf",
        revision: "15f73f5eee9c28f53afefef5723e29680c2fc78a",
        file: "gemma-3-4b-it-q4_0.gguf",
        size: 3155051328,
        sha256: "76aed0a8285b83102f18b5d60e53c70d09eb4e9917a20ce8956bd546452b56e2",
        license: "Gemma",
    },
    Model {
        id: "phi4-mini",
        name: "Phi-4 mini",
        repo: "bartowski/microsoft_Phi-4-mini-instruct-GGUF",
        revision: "7ff82c2aaa4dde30121698a973765f39be5288c0",
        file: "microsoft_Phi-4-mini-instruct-Q4_K_M.gguf",
        size: 2491874688,
        sha256: "01999f17c39cc3074afae5e9c539bc82d45f2dd7faa3917c66cbef76fce8c0c2",
        license: "MIT",
    },
    Model {
        id: "llama3.2-3b",
        name: "Llama 3.2 3B",
        repo: "bartowski/Llama-3.2-3B-Instruct-GGUF",
        revision: "5ab33fa94d1d04e903623ae72c95d1696f09f9e8",
        file: "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        size: 2019377696,
        sha256: "6c1a2b41161032677be168d354123594c0e6e67d2b9227c84f296ad037c728ff",
        license: "Llama 3.2",
    },
];

/// Phi-4 mini is Scribe's default. The VBA editor uses the same file.
pub fn scribe_default() -> Option<&'static Model> {
    CATALOG.iter().find(|model| model.id == "phi4-mini")
}

pub fn verify(spec: &Model, path: &Path) -> Result<(), Error> {
    if std::fs::metadata(path)?.len() != spec.size {
        return Err(Error::Invalid("The model size does not match. Download it again.".into()));
    }
    let (size, hash) = crate::hash_file(path)?;
    if size != spec.size || hash != spec.sha256 {
        return Err(Error::Invalid("The model checksum does not match. Download it again.".into()));
    }
    Ok(())
}

#[cfg(target_arch = "wasm32")]
pub fn download(_spec: &Model, _dir: &Path, _token: &str, _cancel: &AtomicBool, _progress: impl FnMut(u64)) -> Result<std::path::PathBuf, Error> {
    Err(Error::Unsupported)
}

#[cfg(not(target_arch = "wasm32"))]
pub fn download(spec: &Model, dir: &Path, token: &str, cancel: &AtomicBool, progress: impl FnMut(u64)) -> Result<std::path::PathBuf, Error> {
    if cancel.load(Ordering::Relaxed) {
        return Err(Error::Cancelled);
    }
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .https_only(true)
        .timeout_connect(Some(std::time::Duration::from_secs(20)))
        .timeout_global(Some(std::time::Duration::from_secs(7200)))
        .build()
        .into();
    let url = format!("https://huggingface.co/{}/resolve/{}/{}", spec.repo, spec.revision, spec.file);
    let mut request = agent.get(&url);
    // ureq strips Authorization on redirects, including Hugging Face's storage CDN.
    if !token.trim().is_empty() {
        request = request.header("Authorization", format!("Bearer {}", token.trim()));
    }
    let response = request.call().map_err(|e| match e {
        ureq::Error::StatusCode(401 | 403) => Error::Invalid("Accept the model's access terms on Hugging Face and supply a read token.".into()),
        _ => Error::Invalid("The model download failed. Check your connection and try again.".into()),
    })?;
    save_verified(spec, dir, response.into_body().into_reader(), cancel, progress)
}

/// An incomplete file is never installed or offered to inference.
#[cfg(any(not(target_arch = "wasm32"), test))]
fn save_verified(
    spec: &Model,
    dir: &Path,
    mut reader: impl std::io::Read,
    cancel: &AtomicBool,
    mut progress: impl FnMut(u64),
) -> Result<std::path::PathBuf, Error> {
    use sha2::{Digest, Sha256};
    use std::io::Write;
    std::fs::create_dir_all(dir)?;
    let target = dir.join(spec.file);
    let partial = dir.join(format!("{}.{}.part", spec.file, std::process::id()));
    let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&partial)?;
    let result = (|| {
        let mut hash = Sha256::new();
        let mut total = 0u64;
        let mut buffer = [0u8; 65536];
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err(Error::Cancelled);
            }
            let n = reader.read(&mut buffer)?;
            if n == 0 {
                break;
            }
            total = total.checked_add(n as u64).ok_or_else(|| Error::Invalid("Download too large".into()))?;
            if total > spec.size {
                return Err(Error::Invalid("Download too large".into()));
            }
            hash.update(buffer.get(..n).ok_or_else(|| Error::Invalid("Invalid read length".into()))?);
            file.write_all(buffer.get(..n).ok_or_else(|| Error::Invalid("Invalid read length".into()))?)?;
            progress(total);
        }
        if total != spec.size || hash.finalize().iter().map(|b| format!("{b:02x}")).collect::<String>() != spec.sha256 {
            return Err(Error::Invalid("The model checksum does not match. Try downloading again.".into()));
        }
        file.sync_all()?;
        Ok(())
    })();
    drop(file);
    if let Err(e) = result {
        let _ = std::fs::remove_file(&partial);
        return Err(e);
    }
    if let Err(e) = std::fs::rename(&partial, &target) {
        let _ = std::fs::remove_file(&partial);
        return Err(e.into());
    }
    Ok(target)
}

#[cfg(test)]
mod tests {
    use super::*;
    const TINY: Model = Model {
        id: "tiny",
        name: "tiny",
        repo: "test/model",
        revision: "",
        file: "tiny.gguf",
        size: 5,
        sha256: "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
        license: "",
    };
    #[test]
    fn only_verified_downloads_are_installed() {
        let dir = std::env::temp_dir().join(format!("nisaba-download-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let cancel = AtomicBool::new(false);
        let path = save_verified(&TINY, &dir, &b"hello"[..], &cancel, |_| {}).unwrap();
        verify(&TINY, &path).unwrap();
        for bytes in [&b"hellO"[..], &b"hello!"[..], &b"hell"[..]] {
            assert!(save_verified(&TINY, &dir, bytes, &cancel, |_| {}).is_err());
            assert_eq!(std::fs::read(&path).unwrap(), b"hello");
            assert!(!dir.join("tiny.gguf.part").exists());
        }
        cancel.store(true, Ordering::Relaxed);
        assert!(matches!(save_verified(&TINY, &dir, &b"hello"[..], &cancel, |_| {}), Err(Error::Cancelled)));
        assert!(!dir.join("tiny.gguf.part").exists());
        std::fs::remove_dir_all(dir).unwrap();
    }
    /// Public Hugging Face smoke test: fetch one chunk, cancel, and remove the partial.
    #[test]
    #[ignore]
    fn live_download_cancels_without_installing() {
        let spec = CATALOG.first().unwrap();
        let dir = std::env::temp_dir().join(format!("nisaba-live-download-{}", std::process::id()));
        let cancel = AtomicBool::new(false);
        let result = download(spec, &dir, "", &cancel, |_| cancel.store(true, Ordering::Relaxed));
        assert!(matches!(result, Err(Error::Cancelled)), "{result:?}");
        assert!(!dir.join(spec.file).exists());
        assert_eq!(std::fs::read_dir(&dir).unwrap().count(), 0);
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn catalog_has_pinned_files_and_safe_names() {
        for model in CATALOG {
            assert_eq!(model.sha256.len(), 64);
            assert_eq!(model.revision.len(), 40);
            assert!(model.sha256.chars().all(|c| c.is_ascii_hexdigit()));
            assert!(!model.file.contains('/') && !model.file.contains(".."));
            assert!(model.size > 0 && !model.license.is_empty());
        }
        let phi = scribe_default().unwrap();
        assert_eq!(phi.license, "MIT");
        assert_eq!(phi.file, "microsoft_Phi-4-mini-instruct-Q4_K_M.gguf");
    }
}
