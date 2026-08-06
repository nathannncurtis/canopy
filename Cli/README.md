# canopy-cli

`canopy-cli` is the headless scan surface. Run `canopy-cli --help` for options.

Stable exit codes are `0` success, `1` runtime failure, `2` invalid arguments,
`3` partial target failure, `4` all targets failed, and `130` cancellation.
JSON mode writes one summary object to stdout; diagnostics remain on stderr.
