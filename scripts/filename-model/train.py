"""Trains the reading ranker: a softmax over each name's readings, on structural features only.
The objective is convex, so full-batch L-BFGS finds its one optimum and training is reproducible.

train.py --features <features.jsonl>... --labels <labels.jsonl>... --out FilenameModel.Weights.cs [--folds N]
A labels row is {ctx, group, episode, season (optional), end (optional), date (optional), weight (optional), target (optional)}; a null episode means the correct reading has no episode number.
Rows with target true must end up right: those still read wrong are weighted up in later rounds, and the best round is kept."""
import argparse, collections, json, random, sys
import numpy as np
sys.stdout.reconfigure(encoding='utf-8')
ap = argparse.ArgumentParser()
ap.add_argument('--features', nargs='+'); ap.add_argument('--labels', nargs='+'); ap.add_argument('--out')
ap.add_argument('--folds', type=int, default=0); ap.add_argument('--l2', type=float, default=4e-6); ap.add_argument('--iterations', type=int, default=500)
ap.add_argument('--mincount', type=int, default=2); ap.add_argument('--rounds', type=int, default=4); ap.add_argument('--boost', type=float, default=64.0)
a = ap.parse_args()

labels = {}
for path in a.labels:
    for line in open(path, encoding='utf-8'):
        row = json.loads(line); labels[row['ctx']] = row

NUMBERED = {'SeasonEpisode', 'CrossForm', 'SeasonWords', 'Label', 'Bare', 'Compact', 'Pair', 'Special'}
def correct(h, y):
    if y.get('date'):
        return h['form'] == 'Date' and h['date'] == y['date']
    e = y.get('episode')
    if e is None:
        if y.get('film') is True and h['form'] == 'Film': return True
        if y.get('clip') is True: return h['form'] == 'Clip' or (h['form'] == 'Bare' and h['half'])
        return h['form'] in ('None', 'Film', 'Clip') or (h['form'] == 'Bare' and h['half'])
    if h['form'] not in NUMBERED or h['half'] or h['e'] != e: return False
    if y.get('end') is not None and h['end'] != y['end']: return False
    s = y.get('season')
    if s is not None:
        return s == 1 if h['s'] is None else h['s'] == s
    return True

rows = []; unreachable = 0
for path in a.features:
    for line in open(path, encoding='utf-8'):
        row = json.loads(line)
        y = labels.get(row['ctx'])
        if y is None or y.get('type') == 'release_title' or y.get('how') == 'disc': continue
        good = [i for i, h in enumerate(row['h']) if correct(h, y)]
        if not good: unreachable += 1; continue
        rows.append((row['h'], good, y.get('group') or row['ctx'], float(y.get('weight', 1.0)), bool(y.get('target'))))
print('rows', len(rows), 'unreachable', unreachable)
count = collections.Counter(k for hs, *_ in rows for h in hs for k in h['f'])
names = sorted(k for k, v in count.items() if v >= a.mincount)
index = {k: i for i, k in enumerate(names)}
print('features', len(names))

class Data:
    """Flat arrays: one entry per (reading, feature), readings grouped by name."""
    def __init__(self, subset):
        self.starts = []; self.rows_r = []; self.cols = []; self.vals = []; good_mask = []; self.weights = []
        total = 0
        for hs, good, _, weight, _ in subset:
            self.starts.append(total); self.weights.append(weight)
            goodset = set(good)
            for i, h in enumerate(hs):
                good_mask.append(i in goodset)
                for k, v in h['f'].items():
                    j = index.get(k)
                    if j is not None: self.rows_r.append(total + i); self.cols.append(j); self.vals.append(v)
            total += len(hs)
        self.total = total
        self.starts = np.array(self.starts + [total]); self.lengths = np.diff(self.starts)
        self.name_of = np.repeat(np.arange(len(subset)), self.lengths)
        self.rows_r = np.array(self.rows_r, dtype=np.int64); self.cols = np.array(self.cols, dtype=np.int64); self.vals = np.array(self.vals, dtype=np.float64)
        self.good = np.array(good_mask, dtype=bool); self.weights = np.array(self.weights, dtype=np.float64); self.n = len(subset)
    def scores(self, w):
        return np.bincount(self.rows_r, weights=w[self.cols] * self.vals, minlength=self.total)
    def loss_grad(self, w, weights, l2):
        s = self.scores(w)
        top = np.full(self.n, -np.inf); np.maximum.at(top, self.name_of, s)
        e = np.exp(s - top[self.name_of])
        all_sum = np.bincount(self.name_of, weights=e, minlength=self.n)
        good_sum = np.bincount(self.name_of, weights=e * self.good, minlength=self.n)
        p_all = e / all_sum[self.name_of]
        p_good = (e * self.good) / np.maximum(good_sum[self.name_of], 1e-300)
        total_weight = weights.sum()
        loss = -(weights * np.log(np.maximum(good_sum / all_sum, 1e-300))).sum() / total_weight + 0.5 * l2 * (w @ w)
        per_reading = (p_all - p_good) * weights[self.name_of]
        grad = np.bincount(self.cols, weights=per_reading[self.rows_r] * self.vals, minlength=len(w)) / total_weight + l2 * w
        return loss, grad
    def wrong(self, w):
        s = self.scores(w)
        top = np.full(self.n, -np.inf); np.maximum.at(top, self.name_of, s)
        good_top = np.full(self.n, -np.inf); np.maximum.at(good_top, self.name_of[self.good], s[self.good])
        return np.nonzero(good_top < top - 1e-12)[0]

def lbfgs(data, weights, l2, iterations, start=None, memory=30):
    w = np.zeros(len(names)) if start is None else start.copy()
    f, g = data.loss_grad(w, weights, l2)
    S = []; Y = []
    for it in range(iterations):
        q = g.copy(); alphas = []
        for s, y in zip(reversed(S), reversed(Y)):
            alpha = (s @ q) / (y @ s); alphas.append(alpha); q -= alpha * y
        gamma = (S[-1] @ Y[-1]) / (Y[-1] @ Y[-1]) if S else 1.0 / max(1.0, np.linalg.norm(g))
        r = gamma * q
        for (s, y), alpha in zip(zip(S, Y), reversed(alphas)):
            beta = (y @ r) / (y @ s); r += s * (alpha - beta)
        d = -r; slope = g @ d
        if slope >= 0: d = -g; slope = g @ d
        step = 1.0
        for _ in range(30):
            nw = w + step * d; nf, ng = data.loss_grad(nw, weights, l2)
            if nf <= f + 1e-4 * step * slope: break
            step *= 0.5
        else: break
        s = nw - w; y = ng - g
        if s @ y > 1e-12:
            S.append(s); Y.append(y)
            if len(S) > memory: S.pop(0); Y.pop(0)
        improved = f - nf
        w, f, g = nw, nf, ng
        if improved < 1e-9 and it > 20: break
    return w

if a.folds:
    groups = sorted({r[2] for r in rows}); random.Random(7).shuffle(groups)
    fold_of = {g: i % a.folds for i, g in enumerate(groups)}
    hit = 0; total = 0
    for fold in range(a.folds):
        train = Data([r for r in rows if fold_of[r[2]] != fold]); test = Data([r for r in rows if fold_of[r[2]] == fold])
        w = lbfgs(train, train.weights, a.l2, a.iterations)
        missed = len(test.wrong(w)); hit += test.n - missed; total += test.n
        print(f'fold {fold}: held-out reading accuracy {(test.n - missed) / test.n:.4f} on {test.n}')
    print(f'cross-validated by release family {hit / total:.4f}')

data = Data(rows)
weights = data.weights.copy()
target = np.array([r[4] for r in rows], dtype=bool)
def standing(wrong_rows): return (int(target[wrong_rows].sum()), len(wrong_rows))
w = lbfgs(data, weights, a.l2, a.iterations)
wrong = data.wrong(w); best_w = w; best = standing(wrong)
print(f'optimum: {best[1]} of {data.n} read wrong, {best[0]} of them target rows')
# Rare families are few rows among thousands, so target rows still read wrong count for more. The best round is kept.
extra = np.ones(data.n)
for round_ in range(a.rounds):
    if best[0] == 0: break
    missed = wrong[target[wrong]]
    if len(missed) == 0: break
    extra[missed] = np.minimum(a.boost, extra[missed] * 4)
    w = lbfgs(data, weights * extra, a.l2, a.iterations, start=w)
    wrong = data.wrong(w)
    print(f'round {round_}: {standing(wrong)}')
    if standing(wrong) < best: best = standing(wrong); best_w = w
w = best_w
print(f'train reading accuracy {(data.n - best[1]) / data.n:.4f}; target rows wrong {best[0]}; nonzero {int((np.abs(w) > 1e-3).sum())}')
if a.out:
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('namespace BlackPearl.Core;\n\n// Generated by scripts/filename-model/train.py. Do not edit by hand.\ninternal static partial class FilenameModel\n{\n    private static readonly Dictionary<string, float> Weights = new()\n    {\n')
        for k, v in sorted(zip(names, w), key=lambda kv: (-abs(kv[1]), kv[0])):
            if abs(v) >= 1e-3: f.write(f'        ["{k}"] = {v:.4f}f,\n')
        f.write('    };\n}\n')
print(' '.join(f'{k}={v:.2f}' for k, v in sorted(zip(names, w), key=lambda kv: -abs(kv[1]))[:24]))
