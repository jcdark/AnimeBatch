# AnimeBatch — instruções do workspace

App Windows (WinUI 3, .NET) de conversão/upscaling/remux de anime em lote. Substituiu o antigo `converter.py`.

## Layout

- `AnimeBatch.slnx` — solução única.
- `src/AnimeBatch.App` — app WinUI 3 (UI + fila + QueueRunner).
- `src/AnimeBatch.Core` — núcleo (jobs, encodes, ONNX/DirectML, SQLite).
- `src/AnimeBatch.Tests` — xUnit (~135 testes).
- `data/` — SQLite local (`animebatch.db`); migrations versionadas no código.
- `scripts/` — release (`make-release.ps1`) e utilitários.
- `docs/`, `tools/` — documentação e auxiliares.

## Build / teste

```
dotnet build AnimeBatch.slnx
dotnet test  AnimeBatch.slnx
```

- Na SOLUÇÃO, NÃO passe `-p:Platform=x64` — o slnx não declara config de solução x64 e o build morre com MSB4126. O App só tem a plataforma x64 (sempre compila x64) e Core/Tests são AnyCPU rodando em processo x64.
- `-p:Platform=x64` é válido no nível de PROJETO (é o que `scripts/make-release.ps1` usa no publish do App).
- Testes de integração pulam sozinhos se `tools\` não estiver populado (script `setup-tools.ps1`).
- Release: `scripts/make-release.ps1` e depois copiar `data\animebatch.db` da versão anterior.
- NÃO renomear o exe pós-publish (sidecars .deps/.runtimeconfig casam com o nome; usar `AssemblyName=AnimeBatchV$(Version)`).

## Regras do dono (firmes)

- **Encoders congelados** (decisão 17/09/2026): NVENC p7 @500k segue pior que SVT p6 @500k em bitrate baixo e o dono decidiu NÃO mexer — não oferecer híbrido por classe nem logging de comandos.
- Configs antigas sem nova chave devem continuar funcionando (System.Text.Json preserva initializer; defaults ligam recursos novos).
- Sources estão em `G:\Dublados`; finais vão para a raiz do drive de saída (F:\), partes em `F:\AnimeBatch\{base}`.

## Gotchas rápidos

- Reatribuir a MESMA `List<T>` ao ItemsSource é no-op no WinUI → usar `ObservableCollection`.
- `CheckBox` não tem `CheckedChanged` (isso é WPF) → eventos `Checked`/`Unchecked`.
- `x:Bind` default é `OneTime`; label dinâmico pede `Mode=OneWay`.
- Fontes VFR: `-fps_mode cfr -r fps` (senão o áudio desliza).
- Sessão ONNX/DirectML é POR WORKER (`Run` concorrente na mesma sessão deadlocka).

Detalhes completos (histórico de versões, receitas FFmpeg/NVENC, mapa de GPUs) estão na memória do projeto (workspace `E:\AnimeBatch`).
