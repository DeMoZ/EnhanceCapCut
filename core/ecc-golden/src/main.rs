//! `ecc-golden`: the Rust side of the golden-parity runner (design decision 11). Stage 1 grows it to `info`,
//! `dump-timeline` and `dump-writers`; for now it lists a project's timelines.

use std::process::ExitCode;

use ecc::{CoreEnvironment, ProjectLocator};

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("timelines") if args.len() == 2 => {
            let env = CoreEnvironment::default();
            let Some(project) = ProjectLocator::resolve(&args[1], &env.projects_root) else {
                eprintln!("Project not found: {}", args[1]);
                return ExitCode::FAILURE;
            };
            for t in project.timelines() {
                println!("{}{}\t{}", t.name, if t.is_main { " (main)" } else { "" }, t.draft_path.display());
            }
            ExitCode::SUCCESS
        }
        _ => {
            eprintln!("usage: ecc-golden timelines <project>");
            ExitCode::from(2)
        }
    }
}
