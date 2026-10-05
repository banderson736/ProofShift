namespace ProofShift.Projection;

public sealed record ProjectionExecutionOptions
{
    public int WriteBatchSize { get; }

    public ProjectionExecutionOptions(int writeBatchSize = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writeBatchSize);
        WriteBatchSize = writeBatchSize;
    }
}