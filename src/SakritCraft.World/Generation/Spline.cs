namespace SakritCraft.World.Generation;

/// <summary>
/// A piecewise-linear curve over sorted control points, used to map an abstract noise
/// field onto a real quantity such as terrain height in metres.
/// <para>
/// Terrain character lives in these curves rather than in code, so that the shape of
/// the world is tuned by editing data. Changing where continentalness becomes coast,
/// or how steeply mountains rise, is a control point edit and not a recompile.
/// </para>
/// <para>
/// Linear rather than cubic on purpose: a cubic spline can overshoot between control
/// points, and an overshoot in a height curve is a terrain spike nobody asked for.
/// </para>
/// </summary>
public sealed class Spline
{
    private readonly double[] _inputs;
    private readonly double[] _outputs;

    /// <param name="points">Control points as (input, output) pairs. Must be sorted by
    /// input and hold at least two entries.</param>
    public Spline(params (double Input, double Output)[] points)
    {
        if (points.Length < 2)
            throw new ArgumentException("A spline needs at least two control points.", nameof(points));

        _inputs = new double[points.Length];
        _outputs = new double[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0 && points[i].Input <= points[i - 1].Input)
                throw new ArgumentException(
                    $"Control points must be strictly increasing in input; index {i} " +
                    $"({points[i].Input}) does not follow {points[i - 1].Input}.", nameof(points));
            _inputs[i] = points[i].Input;
            _outputs[i] = points[i].Output;
        }
    }

    /// <summary>
    /// Evaluates the curve. Inputs outside the control range clamp to the end values
    /// rather than extrapolating, because extrapolating a height curve off the end of
    /// its range produces terrain far outside the world bounds.
    /// </summary>
    public double Evaluate(double input)
    {
        if (input <= _inputs[0]) return _outputs[0];
        int last = _inputs.Length - 1;
        if (input >= _inputs[last]) return _outputs[last];

        // Binary search for the bracketing segment.
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_inputs[mid] <= input) lo = mid; else hi = mid;
        }

        double t = (input - _inputs[lo]) / (_inputs[hi] - _inputs[lo]);
        return _outputs[lo] + (_outputs[hi] - _outputs[lo]) * t;
    }
}
