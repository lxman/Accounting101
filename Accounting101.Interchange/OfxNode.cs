using System.Xml.Linq;

namespace Accounting101.Interchange;

/// <summary>A read-only navigator over one OFX dialect: pull a leaf value by tag, or the child aggregates
/// by name. Two implementations (<see cref="SgmlOfxNode"/> tolerant 1.x scan, <see cref="XmlOfxNode"/> 2.x
/// XML) let one assembly routine drive both dialects.</summary>
public interface IOfxNode
{
    /// <summary>The first descendant leaf with this tag (trimmed), or null.</summary>
    string? Leaf(string tag);

    /// <summary>The descendant aggregates with this name, each as its own node.</summary>
    IReadOnlyList<IOfxNode> Blocks(string aggregate);
}

/// <summary>1.x SGML navigator — delegates to the tolerant <see cref="OfxScanner"/> over a scope string.</summary>
public sealed class SgmlOfxNode(string scope) : IOfxNode
{
    public string? Leaf(string tag) => OfxScanner.Leaf(scope, tag);

    public IReadOnlyList<IOfxNode> Blocks(string aggregate) =>
        OfxScanner.Blocks(scope, aggregate).Select(s => (IOfxNode)new SgmlOfxNode(s)).ToList();
}

/// <summary>2.x XML navigator over an <see cref="XElement"/>. Matches on local name, case-insensitively, so a
/// namespaced or mixed-case export still reads (OFX 2.x is spec'd unqualified-uppercase).</summary>
public sealed class XmlOfxNode(XElement element) : IOfxNode
{
    public string? Leaf(string tag) =>
        element.Descendants().FirstOrDefault(e => NameMatches(e, tag))?.Value.Trim();

    public IReadOnlyList<IOfxNode> Blocks(string aggregate) =>
        element.Descendants().Where(e => NameMatches(e, aggregate)).Select(e => (IOfxNode)new XmlOfxNode(e)).ToList();

    private static bool NameMatches(XElement e, string tag) =>
        string.Equals(e.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase);
}
