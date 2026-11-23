//! `ecc-golden`: the Rust side of the golden-parity runner (design decision 11). It prints what the native CLIs
//! print, from the Rust core: `info` as `ecc info` does; `dump-timeline` and `dump-writers` come with the golden
//! harness.

use std::process::ExitCode;

use ecc::CoreEnvironment;

const USAGE: &str = "usage: ecc-golden info [project] [--timeline name|number]\n       ecc-golden timelines <project>";

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
