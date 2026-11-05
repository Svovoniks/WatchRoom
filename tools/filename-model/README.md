# Filename reading model

`src/BlackPearl.Core/FilenameModel.Weights.cs` holds the weights that choose between the readings of a filename. This folder trains them.

The model scores structure only: forms, positions, separators, folder ranges and which numbers vary between sibling files (`FilenameFeatures.cs`). It has no input for title words, so it can't memorize a show. A misread name is usually fixed by adding a reading or a feature, not by adding a rule for that name.

## Training

Labelled names stay outside the repository, because they can hold private library paths. Prepare them as JSONL:

- A features file, one line per name, written by `dotnet run --project tests/BlackPearl.Smoke -c Release -- --filename-features <rows.jsonl> <features.jsonl>`. Each input row is `{ "ctx": "<id>", "path": "<relative path>", "kind": "Mixed" }`.
- A labels file, one line per name: `{ "ctx": "<id>", "group": "<release family>", "episode": 3, "season": 2, "end": null, "date": null, "weight": 1 }`. A null `episode` means the correct reading has no episode number. Give `date` as `YYYY-MM-DD` for a daily episode.

Then train, with NumPy installed:

```powershell
py tools/filename-model/train.py --features features.jsonl --labels labels.jsonl --folds 5 --out src/BlackPearl.Core/FilenameModel.Weights.cs
```

`--folds` holds out whole release families (`group`) and reports accuracy on them. Judge a change by that number and by names the model has never seen, not by training accuracy. The objective is convex, so the same data gives the same weights.

## Checking a change

Run `dotnet run --project tests/BlackPearl.Smoke -c Release -- --filename-readings`, then the suites named under Verification in `METADATA.md`.
