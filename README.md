# AdaptiveAPI

**Drop-in multilingual proxy for LLMs, MCP servers, and any HTTP+JSON API.**

Most popular LLMs are trained predominantly on English: ask the same question
in German or Japanese and the answer is measurably worse than in English, on
the same model, for the same money. AdaptiveAPI sits between your app and the
vendor so the LLM keeps seeing English while your user keeps seeing their
language. JSON shapes, code blocks, URLs, tool schemas, and streaming deltas
survive the round trip.

Change your SDK's base URL from `api.openai.com` / `api.anthropic.com` / your
MCP server to AdaptiveAPI. That's it.

## Links

- **Website:** <https://deejaytc.github.io/AdaptiveAPI/>
- **Docs:** <https://deejaytc.github.io/AdaptiveAPI/docs/>

## Quick start

```bash
git clone https://github.com/DeeJayTC/AdaptiveAPI.git
cd AdaptiveAPI/deploy
cp .env.example .env
docker compose up --build
```

- API: <http://localhost:8080> (health at `/healthz`)
- Admin UI: <http://localhost:8000>

See the [docs](https://deejaytc.github.io/AdaptiveAPI/docs/) for SDK usage,
MCP setup, configuration, deployment, and everything else.

## License

[GNU Affero General Public License v3.0](LICENSE).

Copyright © 2026 Tim Cadenbach. `AdaptiveApi.Core` links against the
[DeepL .NET SDK](https://github.com/DeepLcom/deepl-dotnet) (MIT) and other
permissively-licensed dependencies listed in the project files.
