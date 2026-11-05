namespace BlackPearl.Core;

// Scores a reading of a name from its structural features. The weights in FilenameModel.Weights.cs are trained by scripts/filename-model/train.py.
internal static partial class FilenameModel
{
    internal static double Score(List<KeyValuePair<string, float>> features)
    {
        var score = 0.0;
        foreach (var feature in features)
            if (Weights.TryGetValue(feature.Key, out var weight)) score += weight * feature.Value;
        return score;
    }
}
