# Plugin drop-in directory

Drop AdaptiveAPI plugin DLLs into this folder. The docker-compose stack
mounts it read-only at `/app/plugins/` inside the API container, where the
plugin loader scans every `*.dll` at startup.

For the SDK contract, hooks, and a worked example see
[../examples/sample-plugin/](../examples/sample-plugin/).
