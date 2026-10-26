namespace PdfUtil;

/// <summary>The stage of a long-running operation.</summary>
public enum PdfProgressStage
{
    /// <summary>Reading the input and collecting the objects to write.</summary>
    Reading = 0,

    /// <summary>Writing the output objects.</summary>
    Writing = 1,

    /// <summary>Re-reading the output and checking every object (self-verification).</summary>
    Verifying = 2,
}

/// <summary>
/// Progress of a long-running operation. Reports are throttled to about one every 100 ms; the first and last report
/// of each stage are always delivered.
/// </summary>
/// <param name="Stage">Current stage.</param>
/// <param name="Completed">Units (objects) done in this stage.</param>
/// <param name="Total">Units (objects) in this stage; an estimate while reading.</param>
public readonly record struct PdfProgress(PdfProgressStage Stage, long Completed, long Total)
{
    /// <summary>Fraction of the current stage that is done, from 0 to 1.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}
