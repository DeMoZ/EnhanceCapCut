//! `ecc-golden`: the Rust side of the golden-parity runner (design decision 11). It prints what the native CLIs
//! print, from the Rust core: `info` as `ecc info` does, and the canonical `dump-timeline` / `dump-writers` that the
//! hidden commands of both native CLIs print too (see `dump` and docs/DEVELOPMENT.md › Golden parity).

use std::process::ExitCode;

use ecc::{CoreEnvironment, TimelineBuilder};
use ecc_golden::{dump, fixtures};

const USAGE: &str = "usage: ecc-golden info|dump-timeline|dump-writers [project] [--timeline name|number]\n       \
                     ecc-golden timelines <project>\n       ecc-golden wrap <language> < lines";

fn main() -> ExitCode {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        eprintln!("{USAGE}");
        return ExitCode::from(2);
    }
    let command = args.remove(0);
    let timeline = match args.iter().position(|a| a == "--timeline") {
        Some(i) if i + 1 < args.len() => {
            let value = args.remove(i + 1);
            args.remove(i);
            Some(value)
        }
        Some(_) => {
            eprintln!("--timeline needs a value");
            return ExitCode::from(2);
        }
        None => None,
    };
    let env = CoreEnvironment::default();
    let project = args.first().map(String::as_str);
    match command.as_str() {
        "info" => match ecc::info::info(project, timeline.as_deref(), &env) {
            Ok(text) => {
                print!("{text}");
                ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("error: {e}");
                ExitCode::FAILURE
            }
        },
        // Hidden: wraps every stdin line like captions in `language`, line breaks shown as " | ".
        "wrap" => {
            use std::io::BufRead;
            let language = project.unwrap_or("en");
            for line in std::io::stdin().lock().lines().map_while(Result::ok) {
                println!("{}", ecc::subtitles::LineWrapper::wrap(&line, Some(language)).replace('\n', " | "));
            }
            ExitCode::SUCCESS
        }
        "dump-timeline" => match ecc::info::resolve(project, timeline.as_deref(), &env)
            .map_err(|e| e.to_string())
            .and_then(|(p, t)| {
                let built = TimelineBuilder::build_project(&p, Some(&t), &env).map_err(|e| e.to_string())?;
                Ok(dump::dump_timeline(&built, &p.path))
            }) {
            Ok(text) => {
                print!("{text}");
                ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("error: {e}");
                ExitCode::FAILURE
            }
        },
        "dump-writers" => match ecc::info::resolve(project, timeline.as_deref(), &env)
            .map_err(|e| e.to_string())
            .and_then(|(p, t)| dump::dump_writers(&p, &t))
        {
            Ok(text) => {
                print!("{text}");
                ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("error: {e}");
                ExitCode::FAILURE
            }
        },
        // `materialize <fixture> <out> [--content]`: writes a fixture as a project folder (with `draft_content.json` when
        // --content), prints its path.
        "materialize" => {
            let content = args.iter().any(|a| a == "--content");
            let paths: Vec<&String> = args.iter().filter(|a| !a.starts_with("--")).collect();
            let [fixture, out] = paths[..] else {
                eprintln!("usage: ecc-golden materialize <fixture> <out> [--content]");
                return ExitCode::from(2);
            };
            let name = if content { fixtures::DraftName::Content } else { fixtures::DraftName::Info };
            match fixtures::materialize(std::path::Path::new(fixture), std::path::Path::new(out), name) {
                Ok(project) => {
                    println!("{}", project.display());
                    ExitCode::SUCCESS
                }
                Err(e) => {
                    eprintln!("error: {e}");
                    ExitCode::FAILURE
                }
            }
        }
        "timelines" => match ecc::info::resolve(project, None, &env) {
            Ok((project, _)) => {
                for t in project.timelines() {
                    println!("{}{}\t{}", t.name, if t.is_main { " (main)" } else { "" }, t.draft_path.display());
                }
                ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("error: {e}");
                ExitCode::FAILURE
            }
        },
        _ => {
            eprintln!("{USAGE}");
            ExitCode::from(2)
        }
    }
}
