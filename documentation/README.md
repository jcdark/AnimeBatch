# AnimeBatch — Manual do Usuário

* Leia também em [English](README.en-US.md).

Manual completo do **AnimeBatch**, o conversor de anime em lote para Windows
(upscaling + re-encode AV1). Ele é organizado **por menu**, na mesma ordem em que
os itens aparecem na barra lateral do aplicativo, e foi escrito a partir do
vídeo de demonstração gravado pelo autor do sistema combinado com a documentação
interna de cada tela.

> Nas imagens deste manual, nomes de séries, pôsteres e trechos de sinopse foram
> cobertos com mosaico por segurança e respeito a direitos autorais.

---

## Índice

| # | Seção | O que você encontra |
|---|-------|---------------------|
| 1 | [Instalação e conceitos](01-instalacao.md) | Pacote portátil, primeira execução, onde ficam o banco e os arquivos, vocabulário do app (job, parte, capítulo) |
| 2 | [Fila](02-fila.md) | Iniciar/pausar/parar, reordenar, editar itens, VMAF, "Quando terminar", leitura do rodapé |
| 3 | [Episódios](03-episodios.md) | Enfileirar episódios, grade de capítulos, adicionar/editar/excluir, reaproveitamento de partes, reset |
| 4 | [Encodes](04-encodes.md) | Codecs, preset, CQ × bitrate, 2-pass, paralelismo, detecção de cenas, tune/perfil/nível |
| 5 | [Hardware (GPU)](05-hardware.md) | Workers por placa de vídeo, automático × manual |
| 6 | [Séries](06-series.md) | Cadastro, TMDB (busca e ID), bitrates por classe, histórico de conversões |
| 7 | [Configurações](07-configuracoes.md) | Chave do TMDB, idioma, pastas, GPUs de upscaling, base de dados, ferramentas e créditos |

---

## Visão geral do fluxo de trabalho

O ciclo típico de uso tem quatro passos — a maior parte do tempo você vive nas
telas **Episódios** e **Fila**:

1. **Configure uma vez** — Em *Configurações*, aponte a **Pasta Origem Vídeos**
   (onde os downloads aparecem) e a **Pasta de destino das conversões**. Em
   *Séries*, cadastre a série e vincule ao **TMDB** para definir os bitrates por
   classe. Em *Encodes*, confira o codec (o padrão do app é SVT AV1 10 bits em
   modo taxa de bits).
2. **Enfileire** — Na tela *Episódios*, escolha a pasta, marque os arquivos,
   ajuste a grade de capítulos se quiser dividir o episódio (abertura, episódio,
   encerramento...) e clique em **Enfileirar selecionados**.
3. **Converta** — Na tela *Fila*, clique em **Iniciar conversão**. O rodapé mostra
   o andamento em tempo real (parte atual, FPS, velocidade, previsão de término).
   Opcional: **Verificar qualidade (VMAF)** mede a qualidade de cada parte ao
   final, e **Quando terminar** desliga/hiberna a máquina sozinho.
4. **Receba os arquivos** — O arquivo final vai para a raiz da pasta de destino;
   as partes intermediárias ficam em `destino\AnimeBatch\{nome do episódio}` e
   podem ser reaproveitadas (sem re-encode) se você converter o mesmo episódio
   de novo.

---

## Navegação

- Use o índice acima ou as setas de navegação no topo/rodapé de cada página
  (**← anterior · Índice · próxima →**).
- Os nomes de botões e campos estão sempre em **negrito** exatamente como
  aparecem na tela (em português).
- Prefixos de tecla como `mm:ss` indicam formato de digitação.

---

*[Próxima: Instalação e conceitos →](01-instalacao.md)*
