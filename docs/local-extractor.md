# Local extractor process

The `kb extract --source <uuid>` command does not start an extractor unless it is configured explicitly.

Configure the executable and argument array in `~/.kb/appsettings.json` or the environment. Environment values override the JSON file. See [Application configuration](application-configuration.md) for the JSON form. The executable and each argument remain separate settings:

```sh
export KB_LOCAL_EXTRACTOR_EXECUTABLE=/path/to/extractor
export KB_LOCAL_EXTRACTOR_ARGUMENTS='["--stdio","--format","json"]'
export KB_LOCAL_EXTRACTOR_MODEL_VERSION='model-or-adapter-version'
```

`KB_LOCAL_EXTRACTOR_ARGUMENTS` must be a JSON array of strings. The process receives one JSON request on stdin and returns an object with `schema_version: "1"` and a `candidates` array. Each candidate needs a title, body, exact quote, and inclusive one-based `start_line` and `end_line`; `type` and `kind` default to `fact` and `source_claim`.

The command limits input to 10 MiB, execution to 120 seconds, stdout to 4 MiB, stderr to 64 KiB, and the response to 50 candidates. It validates citation ranges and quotes before saving candidates. Invalid candidates are reported separately; valid drafts that were already saved remain available if a later save fails. Every created draft records the operation ID, adapter version, optional model version, and source text hash. Secrets and the full request are not saved in record metadata.

“Local” only describes where the child process is launched. This application does not sandbox the process or prevent it from accessing the network, files, or other local resources. Configure and run only an executable whose behavior is trusted for the source material being processed.
