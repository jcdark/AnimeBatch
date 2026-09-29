# 5. Hardware (GPU)

*[← Anterior: Encodes](04-encodes.md) · [Índice](README.md) · [Próxima: Séries →](06-series.md)*

---

A aba **Hardware (GPU)** lista as **placas de vídeo dedicadas** (PCI) do
computador — as integradas na CPU ficam de fora — e permite dizer **quantos
workers** cada uma deve usar.

![Aba Hardware com duas GPUs](images/hardware-gpus.png)

## O que é um worker

Worker = um **processamento paralelo na placa**. Os workers valem para:

- o **upscaling** (Real-CUGAN, Real-ESRGAN, AnimeJaNai) — cada worker processa
  um trecho do vídeo em paralelo; e
- o **encode AV1 NVENC** — as partes do episódio são codificadas em paralelo,
  uma por worker.

É possível combinar livremente (2 só na placa principal, 2 em ambas etc.) e
até definir **0 workers — não usar esta placa**.

## Automático × manual

- **Usar automático** — o app detecta as placas NVIDIA, dá um worker a cada
  uma e um extra à principal. É o suficiente para quase todo mundo.
- **Salvar configuração** — trava a escolha manual por placa. A mudança vale
  **a partir do próximo job da fila** (o job em execução não é tocado).

Quando configurado aqui, este ajuste **tem precedência** sobre o campo
*GPUs para upscaling* das [Configurações](07-configuracoes.md).

## Números reais (máquina do autor)

Com uma RTX 5060 Ti dedicada só ao app e a RTX 4060 Ti cuidando da área de
trabalho (1 worker):

| Configuração de upscaling | Velocidade |
|---------------------------|------------|
| 1 worker na 5060 Ti | ~15 fps |
| 2 workers na 5060 Ti | ~28 fps |
| Duas placas (2 + 1 workers) | ~36 fps |

Para se situar: um episódio de anime tem 23.976 fps — acima disso o
upscaling corre **mais rápido que o tempo real** (uma temporada em horas, não
dias).

---

*[← Anterior: Encodes](04-encodes.md) · [Índice](README.md) · [Próxima: Séries →](06-series.md)*
