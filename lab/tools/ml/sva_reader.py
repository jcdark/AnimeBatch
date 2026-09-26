# Leitor dos dumps de decisao do fork AnimeBatch do SVT-AV1 (formato v3).
# Monta o dataset de RANKING sobre o array completo de candidatos rapidos
# (type 5, ANTES do MDS0 avaliar): para cada CU amostrado, as linhas sao as
# definicoes de candidato e o alvo e fast_win (indice do vencedor real).
#
# Uso: python sva_reader.py <dir_run1> [<dir_run2> ...] -o dataset.npz
import struct
import math
import glob
import os
import argparse

import numpy as np

HDR = struct.Struct("<BBHHI")            # type, version, cu_size, cand_size, reserved (10B)
FRAME = struct.Struct("<BBBBHHQ")        # 16B
CU = struct.Struct("<9B4H2hBBQQ")        # 39B (v3: + fast_win)
CAND = struct.Struct("<BBBBB3x2hQ")      # 20B
FAST = struct.Struct("<BBBB4x2hQ")       # 24B: type,class,mode,ref0,pad4,mvx,mvy,fast_cost(0)


def read_dump(path, clip_id):
    rows = []
    with open(path, "rb") as f:
        data = f.read()
    off = 0
    t, ver, cu_sz, cand_sz, _ = HDR.unpack_from(data, off)
    off += HDR.size
    assert t == 0 and ver == 3, f"header inesperado em {path} (v{ver})"

    pic = 0
    fast_cands = []      # linhas type 5: array completo pre-MDS0
    cu_total = 0
    dropped = 0
    while off < len(data):
        rtype = data[off]
        if rtype == 1:
            (_, slice_type, qp, _, w, h, pic) = FRAME.unpack_from(data, off)
            off += FRAME.size
        elif rtype == 5:
            (_t, cclass, mode, ref0, mv_x, mv_y, fast) = FAST.unpack_from(data, off)
            off += FAST.size
            fast_cands.append((cclass, mode, ref0, abs(mv_x), abs(mv_y)))
        elif rtype == 2:
            (t2, cclass, mode, slice_type, qp, pd_pass, shape, skip, n_list,
             org_x, org_y, bsize, fast_win, mv_x, mv_y, ref0, _pad, rd, p) = CU.unpack_from(data, off)
            off += CU.size
            cu_total += 1
            if fast_cands and pd_pass == 1 and fast_win != 0xFFFF and fast_win < len(fast_cands):
                rows.append(dict(
                    clip=clip_id, pic=pic, slice=slice_type, qp=qp, shape=shape,
                    bsize=bsize, org_x=org_x, org_y=org_y, n_fast=len(fast_cands),
                    cands=fast_cands, winner=fast_win))
            fast_cands = []
        elif rtype in (3, 4):
            off += CAND.size if rtype == 3 else FAST.size
        else:
            raise ValueError(f"tipo de registro desconhecido {rtype} em {path}@{off}")
    return rows, dict(cu_total=cu_total, dropped=dropped)


def build_arrays(all_rows):
    feat_names = ["c_class", "c_mode", "c_ref0", "c_absmvx", "c_absmvy",
                  "cu_bsize", "cu_shape", "cu_qp", "cu_slice", "cu_sb_x", "cu_sb_y", "cu_n_fast"]
    X, y, gid, rank = [], [], [], []
    for gid_i, g in enumerate(all_rows):
        for pos, (cclass, mode, ref0, mvx, mvy) in enumerate(g["cands"]):
            X.append([cclass, mode, ref0, min(mvx, 8191), min(mvy, 8191),
                      g["bsize"], g["shape"], g["qp"], g["slice"],
                      g["org_x"] % 64, g["org_y"] % 64, g["n_fast"]])
            y.append(1 if pos == g["winner"] else 0)
            gid.append(gid_i)
            rank.append(pos)
    return (np.asarray(X, dtype=np.float32), np.asarray(y, dtype=np.int8),
            np.asarray(gid, dtype=np.int64), np.asarray(rank, dtype=np.int32), feat_names)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("-o", "--out", required=True)
    args = ap.parse_args()

    all_rows = []
    for run in args.runs:
        clip_id = os.path.basename(run.rstrip("/\\"))
        dumps = sorted(glob.glob(os.path.join(run, "sva_dump_*.bin")))
        rows = []
        cu_total = 0
        for d in dumps:
            r, s = read_dump(d, clip_id)
            rows.extend(r)
            cu_total += s["cu_total"]
        print(f"{clip_id}: {cu_total} CUs, {len(rows)} grupos de ranking")
        all_rows.extend(rows)

    X, y, gid, rank, names = build_arrays(all_rows)
    print(f"total: {len(all_rows)} CUs, {X.shape[0]} linhas de candidato, {X.shape[1]} features")
    np.savez_compressed(args.out, X=X, y=y, gid=gid, rank=rank,
                        names=np.array(names), n_groups=len(all_rows))
    print(f"salvo em {args.out}")


if __name__ == "__main__":
    main()