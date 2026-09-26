# Treino do ranker de candidatos (Fase 3) + metrica go/no-go recall@k.
#
# Modelo: MLP pequeno que, dado um CU e um candidato (features baratas, SEM o
# custo avaliado do RDO), produz um logit; softmax dentro do CU (ranking
# listwise) mirando o vencedor real do encoder.
#
# Baselines honestos:
#   - ordem original da lista (o propio encoder ja ordena por custo rapido)
#   - aleatorio
#
# Uso: python train_rank.py dataset.npz --epochs 12 --k 1 3 5 15
import argparse
import os
from collections import defaultdict
import numpy as np
import torch
import torch.nn as nn


class Ranker(nn.Module):
    def __init__(self, dim, hidden=64):
        super().__init__()
        self.net = nn.Sequential(
            nn.Linear(dim, hidden), nn.ReLU(),
            nn.Linear(hidden, hidden), nn.ReLU(),
            nn.Linear(hidden, 1))

    def forward(self, x):
        return self.net(x).squeeze(-1)


def load_groups(path, max_groups=None, seed=42):
    z = np.load(path, allow_pickle=False)
    X, y, gid, rank = z["X"], z["y"], z["gid"], z["rank"]
    n_groups = int(z["n_groups"])

    # agrupamento VETORIZADO (np.where por grupo seria O(grupos*linhas) e nao termina)
    order = np.argsort(gid, kind="stable")
    gsorted = gid[order]
    starts = np.searchsorted(gsorted, np.arange(n_groups), side="left")
    ends = np.searchsorted(gsorted, np.arange(n_groups), side="right")

    Xn = (X - X.mean(axis=0)) / (X.std(axis=0) + 1e-6)
    mu, sd = X.mean(axis=0), X.std(axis=0)

    groups = []
    for g in range(n_groups):
        idx = order[starts[g]:ends[g]]
        if len(idx) == 0:
            continue
        groups.append((Xn[idx], y[idx], rank[idx]))

    # subsample estatisticamente suficiente quando o dataset e enorme
    if max_groups is not None and len(groups) > max_groups:
        rng = np.random.default_rng(seed)
        pick = rng.permutation(len(groups))[:max_groups]
        groups = [groups[i] for i in pick]
    return groups, mu, sd, X, y


def eval_recall(model, groups, ks, device, max_len=None):
    hits = {k: 0 for k in ks}
    total = 0
    pos0 = {k: 0 for k in ks}  # baseline: posicao 0 da lista (ordem do encoder)
    with torch.no_grad():
        for Xg, yg, rg in groups:
            logits = model(torch.from_numpy(Xg).to(device)).cpu().numpy()
            order = np.argsort(-logits)
            winner_pos = int(np.argmax(yg == 1)) if yg.max() == 1 else -1
            if winner_pos < 0:
                continue
            total += 1
            model_rank = int(np.where(order == winner_pos)[0][0]) + 1
            enc_rank = int(np.where(rg == winner_pos)[0][0]) + 1
            for k in ks:
                if model_rank <= k:
                    hits[k] += 1
                if enc_rank <= k:
                    pos0[k] += 1
    return {k: hits[k] / max(1, total) for k in ks}, \
           {k: pos0[k] / max(1, total) for k in ks}, total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset")
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch-groups", type=int, default=256)
    ap.add_argument("--hidden", type=int, default=64)
    ap.add_argument("--k", type=int, nargs="+", default=[1, 3, 5, 15])
    ap.add_argument("--val-frac", type=float, default=0.2)
    ap.add_argument("--save", default="", help="salvar pesos (.pt)")
    ap.add_argument("--export-prior", action="store_true", help="imprimir tabela P(vencedor|classe,modo) no stdout (entre marcadores)")
    ap.add_argument("--max-groups", type=int, default=400000, help="subsampling de CUs (estatistica basta)")
    args = ap.parse_args()

    groups, mu, sd, X_raw, y_raw = load_groups(args.dataset, max_groups=args.max_groups)
    rng = np.random.default_rng(42)
    perm = rng.permutation(len(groups))
    n_val = max(1, int(len(groups) * args.val_frac))
    val = [groups[i] for i in perm[:n_val]]
    train = [groups[i] for i in perm[n_val:]]
    print(f"grupos: {len(train)} treino / {len(val)} validacao")

    device = "cuda" if torch.cuda.is_available() else "cpu"
    model = Ranker(groups[0][0].shape[1], args.hidden).to(device)
    opt = torch.optim.Adam(model.parameters(), lr=1e-3)
    ce = nn.CrossEntropyLoss()

    # batches de grupos com padding
    def batches(data):
        for i in range(0, len(data), args.batch_groups):
            chunk = data[i:i + args.batch_groups]
            w = max(len(x) for x, _, _ in chunk)
            xb = np.zeros((len(chunk), w, X_DIM), dtype=np.float32)
            mask = np.zeros((len(chunk), w), dtype=bool)
            tgt = np.full(len(chunk), -1, dtype=np.int64)
            for j, (x, y, _r) in enumerate(chunk):
                xb[j, :len(x)] = x
                mask[j, :len(x)] = True
                widx = np.where(y == 1)[0]
                if len(widx):
                    tgt[j] = widx[0]
            yield (torch.from_numpy(xb).to(device),
                   torch.from_numpy(mask).to(device),
                   torch.from_numpy(tgt).to(device))

    global X_DIM
    X_DIM = groups[0][0].shape[1]

    for epoch in range(1, args.epochs + 1):
        model.train()
        perm_t = rng.permutation(len(train))
        loss_sum, n = 0.0, 0
        for xb, mask, tgt in batches([train[i] for i in perm_t]):
            logits = model(xb)
            logits = logits.masked_fill(~mask, -1e9)
            loss = ce(logits, tgt)
            opt.zero_grad()
            loss.backward()
            opt.step()
            loss_sum += float(loss) * len(tgt)
            n += len(tgt)
        if epoch % 3 == 0 or epoch == 1:
            print(f"epoch {epoch}: loss {loss_sum / max(1, n):.4f}")

    model.eval()
    rec, enc, total = eval_recall(model, val, args.k, device)
    print(f"\n=== recall@k na validacao ({total} CUs) ===")
    for k in args.k:
        print(f"  k={k:>2}: modelo {rec[k]*100:6.2f}%  | ordem original do encoder {enc[k]*100:6.2f}%")
    if args.save:
        torch.save({"state": model.state_dict(), "mu": mu, "sd": sd}, args.save)
        print(f"pesos: {args.save}")

    if args.export_prior:
        # tabela de prior P(vencedor | classe, modo) usada pelo fork (SVA_AI_PRIOR).
        # Sai no STDOUT entre marcadores (o chamador redireciona para o arquivo);
        # pares ausentes ficam com prior neutro 0.06 no C (nem promovido nem podado)
        wins = defaultdict(float)
        tot = defaultdict(float)
        ccol, mcol = X_raw[:, 0].astype(int), X_raw[:, 1].astype(int)
        for c, m, w in zip(ccol, mcol, y_raw):
            wins[(c, m)] += float(w)
            tot[(c, m)] += 1.0
        lines = sorted((c, m, wins[(c, m)] / tot[(c, m)]) for (c, m) in tot)
        print("---AI_PRIOR---")
        for c, m, p in lines:
            print(f"{c} {m} {p:.6f}")
        print("---END_PRIOR---")
        print(f"tabela de prior: {len(lines)} pares")


if __name__ == "__main__":
    main()