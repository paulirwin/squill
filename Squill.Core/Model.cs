namespace Squill.Core;

public class Model : IHashable
{
    public IList<Element> Elements { get; } = new List<Element>();

    public byte[] Hash => HashUtility.Compute(Elements);
}