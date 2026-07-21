namespace Squill.Core;

/// <summary>
/// A requirement that one element be created after another, and the constraint that
/// imposes it — a table must follow the table its foreign key references.
///
/// The constraint is carried alongside the dependency so that a circular reference, which
/// no create order can satisfy, can be broken by deferring exactly that constraint rather
/// than the whole table.
/// </summary>
/// <param name="DependsOn">The element that must be created first.</param>
/// <param name="Constraint">
/// The constraint imposing the requirement (a foreign key), or <c>null</c> when the
/// dependency is inherent to the element and cannot be deferred.
/// </param>
public readonly record struct CreateDependency(Element DependsOn, Element? Constraint = null);
