fn main() {
    slint_build::compile("ui/app.slint").expect("Slint UI compiles");
    // Windows: the app icon and a manifest (per-monitor DPI awareness, Windows 10/11 compatibility).
    embed_resource::compile("assets/app.rc", embed_resource::NONE).manifest_required().expect("resources compile");
}
