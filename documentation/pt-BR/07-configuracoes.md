# 7. Configurações

*[← Anterior: Séries](06-series.md) · [Índice](README.md)*

---

A aba **Configurações** concentra as preferências globais do app — configure
aqui uma vez e o resto do uso flui.

![Topo da aba Configurações](../images/configuracoes-geral.png)

## Integrações — chave do TMDB

A **Chave da API do TMDB (v3)** habilita a busca de séries, pôsteres e
sinopses na tela [Séries](06-series.md). Crie uma conta gratuita em
[themoviedb.org](https://www.themoviedb.org/) → *Configurações → API*, cole a
chave e clique em **Salvar chave**. Sem chave, esses recursos ficam
desativados (o resto do app funciona normalmente).

## Idioma do sistema

*Português (Brasil)* ou *Inglês* — aplicado na hora. Para incluir um idioma
novo, basta colocar um `.json` na pasta `i18n\` do app.

## Pastas

- **Pasta Origem Vídeos** — de onde a aba [Episódios](03-episodios.md) carrega
  os vídeos automaticamente ao abrir (você ainda pode trocar a pasta lá, e a
  última pasta usada é memorizada).
- **Pasta de destino das conversões** — onde os resultados são gravados: os
  **arquivos finais** na raiz da pasta e as **partes intermediárias** em
  `destino\AnimeBatch\{episódio}\` (ver
  [Instalação e conceitos](01-instalacao.md)).

## GPUs para upscaling (Vulkan/DirectML)

Deixando **vazio (`auto`)**, o app detecta as placas NVIDIA e distribui os
workers automaticamente. Para escolher à mão, informe os **índices separados
por vírgula** (repetir um índice dá mais workers àquela placa, ex.: `0,2,0`).
No motor ncnn os índices são Vulkan; no ONNX são DXGI/DirectML — a lista
*Detectadas* abaixo do campo mostra o que existe na máquina. Quando a aba
[Hardware (GPU)](05-hardware.md) estiver configurada, **ela tem precedência**
sobre este campo.

## Base de dados

Mostra onde o banco vive (`%LOCALAPPDATA%\AnimeBatch\animebatch.db`) e o
tamanho atual. O botão **Limpar base de dados** apaga séries, episódios, fila
e histórico de conversões — **mantendo as preferências** (idioma, pastas,
chave TMDB e configurações de encode). A ação pede confirmação e é bloqueada
com a fila em execução. As grades de capítulos editadas
(`chapters-edits\`, ao lado do banco) também ficam no AppData e sobrevivem a
reinstalações.

## Ferramentas embarcadas e créditos

![Lista de ferramentas e créditos](../images/configuracoes-creditos.png)

A lista final mostra cada ferramenta que vem dentro do pacote e o papel dela —
os nomes são links para os projetos originais:

| Ferramenta | Papel |
|------------|-------|
| **FFmpeg / ffprobe** | encode de vídeo/áudio, sondagem e remux |
| **MKVToolNix** (mkvmerge/mkvextract) | junção das partes e capítulos no arquivo final |
| **HandBrake (HandBrakeCLI)** | engine de encode CLI |
| **SVT-AV1 (SvtAv1EncApp)** | encoder AV1 software (SVT) |
| **Av1an** | fatiamento por cena e chunks em paralelo |
| **VapourSynth** | frameserver usado pelo motor Av1an |
| **Real-CUGAN** (ncnn Vulkan) | upscaling de vídeo |
| **Real-ESRGAN** (ncnn Vulkan) | upscaling de vídeo |
| **AnimeJaNai** (modelos ONNX) | upscaling ONNX (DirectML) |

---

*[← Índice](README.md)*
