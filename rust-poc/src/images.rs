//! Product thumbnails: fetched once, kept on disk (same file names as the .NET app's cache) and decoded on worker
//! threads at 112 px wide. Failures just mean no picture.

use crate::api::Client;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::mpsc::{channel, Sender};
use std::sync::{Arc, Mutex};

pub const DECODE_WIDTH: u32 = 112;
const WORKERS: usize = 2;

/// A decoded thumbnail, ready to become a `slint::Image` on the UI thread.
pub struct Decoded {
    pub url: String,
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

pub struct Loader {
    cache_dir: PathBuf,
    queue: Sender<String>,
    pending: Arc<Mutex<HashSet<String>>>,
}

impl Loader {
    /// `deliver` runs on a worker thread; it should hand the picture to the UI thread.
    pub fn new(client: Client, cache_dir: PathBuf, deliver: impl Fn(Decoded) + Send + Sync + 'static) -> Self {
        let (tx, rx) = channel::<String>();
        let rx = Arc::new(Mutex::new(rx));
        let deliver = Arc::new(deliver);
        let pending = Arc::new(Mutex::new(HashSet::new()));
        for _ in 0..WORKERS {
            let (rx, client, dir, deliver, pending) = (rx.clone(), client.clone(), cache_dir.clone(), deliver.clone(), pending.clone());
            std::thread::Builder::new()
                .name("images".into())
                .spawn(move || loop {
                    let url = match rx.lock().unwrap().recv() {
                        Ok(u) => u,
                        Err(_) => return,
                    };
                    if let Some(decoded) = load(&client, &dir, &url) {
                        deliver(decoded);
                    }
                    pending.lock().unwrap().remove(&url);
                })
                .expect("image thread");
        }
        Self { cache_dir, queue: tx, pending }
    }

    /// Asks for a picture; nothing happens if it is already on the way.
    pub fn request(&self, url: &str) {
        if self.pending.lock().unwrap().insert(url.to_string()) {
            let _ = self.queue.send(url.to_string());
        }
    }

    pub fn file_for(&self, url: &str) -> PathBuf {
        file_for(&self.cache_dir, url)
    }

    /// Forgets one picture on disk (an unpinned product leaves nothing behind).
    pub fn delete(&self, url: &str) {
        let _ = std::fs::remove_file(self.file_for(url));
    }

    /// Deletes every cached picture except `keep`, e.g. search thumbnails from earlier runs.
    pub fn prune(&self, keep: &[String]) {
        let keep: HashSet<PathBuf> = keep.iter().map(|u| self.file_for(u)).collect();
        let Ok(entries) = std::fs::read_dir(&self.cache_dir) else { return };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().map_or(false, |e| e == "img") && !keep.contains(&path) {
                let _ = std::fs::remove_file(path);
            }
        }
    }
}

fn file_for(dir: &PathBuf, url: &str) -> PathBuf {
    let hash = Sha256::digest(url.as_bytes());
    let hex: String = hash.iter().map(|b| format!("{b:02X}")).collect();
    dir.join(format!("{}.img", &hex[..32]))
}

fn load(client: &Client, dir: &PathBuf, url: &str) -> Option<Decoded> {
    let file = file_for(dir, url);
    let bytes = match std::fs::read(&file) {
        Ok(b) => b,
        Err(_) => {
            let b = client.get_bytes(url).ok()?;
            let _ = std::fs::create_dir_all(dir);
            let _ = std::fs::write(&file, &b);
            b
        }
    };
    let img = image::load_from_memory(&bytes).ok()?;
    let (w, h) = (img.width(), img.height());
    let img = if w > DECODE_WIDTH {
        let nh = (h as f32 * DECODE_WIDTH as f32 / w as f32).round().max(1.0) as u32;
        img.resize_exact(DECODE_WIDTH, nh, image::imageops::FilterType::Triangle)
    } else {
        img
    };
    let rgba = img.into_rgba8();
    Some(Decoded { url: url.to_string(), width: rgba.width(), height: rgba.height(), rgba: rgba.into_raw() })
}
