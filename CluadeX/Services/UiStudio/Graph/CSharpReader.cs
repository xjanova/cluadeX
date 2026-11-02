using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// C# through Roslyn: every .cs file is parsed into a real syntax tree (so comments, strings and <c>#if false</c>
/// blocks are never code) and bound in one compilation of the project's own sources. A base type, a call, a
/// <c>new</c>, a type named anywhere, and a DI registration (<c>AddSingleton&lt;IFoo, Foo&gt;()</c>) become edges
/// to the file that declares that exact symbol — for a call, the file of that method, even in a partial class
/// split over many files. Names the compiler could not bind but that match exactly one project type are kept as
/// inferred edges (a missing using, a type in a project the graph did not read).
/// </summary>
internal static class CSharpReader
{
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular, new[] { "DEBUG", "TRACE" });

    private static readonly HashSet<string> DiMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddTransient", "AddScoped", "TryAddSingleton", "TryAddTransient", "TryAddScoped", "AddHostedService",
        "AddKeyedSingleton", "AddKeyedScoped", "AddKeyedTransient", "AddDbContext", "AddDbContextFactory", "AddHttpClient",
        "TryAddEnumerable", "RegisterType", "RegisterSingleton", "RegisterInstance", "Register", "As",
    };

    private static IReadOnlyList<MetadataReference>? _refs;
    private static IReadOnlyList<MetadataReference> References => _refs ??= LoadReferences();

    /// <summary>The runtime's own core assemblies, so List&lt;Foo&gt;, LINQ and friends bind and the project's types
    /// inside them stay visible. Nothing of the project's packages is needed: edges only go to project sources.</summary>
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var list = new List<MetadataReference>();
        string dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var name in new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "System.Console.dll",
                     "System.ComponentModel.dll", "System.ComponentModel.Primitives.dll", "System.ObjectModel.dll", "System.Threading.Tasks.dll", "netstandard.dll" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) try { list.Add(MetadataReference.CreateFromFile(p)); } catch { }
        }
        return list;
    }

    public static CSharpModel? Read(GraphContext cx)
    {
        var files = cx.OfExt(".cs").ToList();
        if (files.Count == 0) return null;
        var trees = new List<SyntaxTree>(files.Count);
        var fileOf = new Dictionary<SyntaxTree, SourceFile>();
        foreach (var f in files)
        {
            cx.Ct.ThrowIfCancellationRequested();
            if (f.Parsed is not SyntaxTree tree)
            {
                tree = CSharpSyntaxTree.ParseText(f.Text, Parse, f.Full, cancellationToken: cx.Ct);
                f.Parsed = tree;   // kept with this version of the file: an unchanged file is not parsed again
            }
            trees.Add(tree);
            fileOf[tree] = f;
            cx.Graph.ById[f.Id].Kind = GraphNodeKind.Code;
        }

        // names declared in the project: only identifiers spelled like one are bound (binding every name is slow)
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        var bySimple = new Dictionary<string, List<(SourceFile File, int Line)>>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var f = fileOf[tree];
            foreach (var node in tree.GetRoot(cx.Ct).DescendantNodes(n => n is not BlockSyntax and not ArrowExpressionClauseSyntax))
            {
                switch (node)
                {
                    case BaseTypeDeclarationSyntax t:
                        typeNames.Add(t.Identifier.Text);
                        if (!bySimple.TryGetValue(t.Identifier.Text, out var l)) bySimple[t.Identifier.Text] = l = new();
                        l.Add((f, LineOf(t.Identifier)));
                        break;
                    case DelegateDeclarationSyntax d: typeNames.Add(d.Identifier.Text); break;
                    case MethodDeclarationSyntax m: methodNames.Add(m.Identifier.Text); break;
                }
            }
        }

        var compilation = CSharpCompilation.Create("cxgraph", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false, nullableContextOptions: NullableContextOptions.Disable));
        var model = new CSharpModel(compilation, fileOf, bySimple);

        // Binding is the cost (seconds on a few hundred files), so what a file binds to is kept between builds. It can
        // only change when that file changed, or when what the project declares changed (a type, a member signature, a
        // using — everything outside method bodies: the "shape"). An edit inside a method body, the usual one, binds
        // that one file again; any other change binds them all.
        long shape = ProjectShape(files);
        Dictionary<string, (SyntaxTree Tree, FileRead Read)>? before = null;
        lock (Cache)
            if (Cache.TryGetValue(cx.FullRoot, out var c) && c.Shape == shape) before = c.Files;
        var reads = new ConcurrentDictionary<SourceFile, FileRead>();
        var toBind = new List<SyntaxTree>();
        foreach (var tree in trees)
        {
            var f = fileOf[tree];
            if (before != null && before.TryGetValue(f.Full, out var old) && ReferenceEquals(old.Tree, tree)) reads[f] = old.Read;
            else toBind.Add(tree);
        }
        // files are bound in parallel; their edges are added afterwards on this thread, in file order
        Parallel.ForEach(toBind, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 3), CancellationToken = cx.Ct }, tree =>
        {
            var f = fileOf[tree];
            var read = new FileRead();
            ReadTree(cx, model, compilation.GetSemanticModel(tree), tree, f, typeNames, methodNames, read);
            reads[f] = read;
        });
        model.Rebound = toBind.Count;
        foreach (var f in files)
        {
            if (!reads.TryGetValue(f, out var read)) continue;
            foreach (var e in read.Edges) cx.Edge(f, e.To, e.Kind, e.Line, e.Inferred, e.Why, e.Label);
            foreach (var (view, vm, line) in read.DataContexts) model.DataContexts.TryAdd(view, (vm, f, line));
        }
        lock (Cache)
        {
            Cache[cx.FullRoot] = (shape, files.ToDictionary(f => f.Full, f => ((SyntaxTree)f.Parsed!, reads[f]), StringComparer.OrdinalIgnoreCase));
            CacheOrder.Remove(cx.FullRoot);
            CacheOrder.Add(cx.FullRoot);
            while (CacheOrder.Count > 2) { Cache.Remove(CacheOrder[0]); CacheOrder.RemoveAt(0); }
        }
        return model;
    }

    /// <summary>What one file binds to: its edges and the DataContexts it sets in code.</summary>
    private sealed class FileRead
    {
        public List<EdgeRec> Edges { get; } = new();
        public List<(string View, string Vm, int Line)> DataContexts { get; } = new();
    }

    private readonly record struct EdgeRec(string To, GraphEdgeKind Kind, int Line, bool Inferred, string? Why, string? Label);

    /// <summary>The last two projects' reads (the chat's project and the one before), by shape.</summary>
    private static readonly Dictionary<string, (long Shape, Dictionary<string, (SyntaxTree Tree, FileRead Read)> Files)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> CacheOrder = new();
    private static readonly ConditionalWeakTable<SyntaxTree, StrongBox<long>> Shapes = new();

    /// <summary>For the harness: forget the kept reads.</summary>
    internal static void ClearCache() { lock (Cache) { Cache.Clear(); CacheOrder.Clear(); } }

    /// <summary>A hash of every file's path and its tokens outside method, accessor and lambda bodies.</summary>
    private static long ProjectShape(List<SourceFile> files)
    {
        ulong h = 14695981039346656037;
        void Mix(string s) { foreach (char ch in s) { h ^= ch; h *= 1099511628211; } h ^= 0xFF; h *= 1099511628211; }
        foreach (var f in files.OrderBy(f => f.Full, StringComparer.OrdinalIgnoreCase))
        {
            var tree = (SyntaxTree)f.Parsed!;
            var box = Shapes.GetValue(tree, t =>
            {
                ulong fh = 14695981039346656037;
                foreach (var tok in t.GetRoot().DescendantTokens(n => n is not BlockSyntax and not ArrowExpressionClauseSyntax))
                    foreach (char ch in tok.Text) { fh ^= ch; fh *= 1099511628211; }
                return new StrongBox<long>((long)fh);
            });
            Mix(f.Full);
            Mix(box.Value.ToString());
        }
        return (long)h;
    }

    private static int LineOf(SyntaxToken t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static int LineOf(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static void ReadTree(GraphContext cx, CSharpModel model, SemanticModel sm, SyntaxTree tree, SourceFile file,
        HashSet<string> typeNames, HashSet<string> methodNames, FileRead read)
    {
        var root = tree.GetRoot(cx.Ct);
        void Add(string toId, GraphEdgeKind kind, SyntaxNode at, bool inferred = false, string? why = null, string? label = null)
            => read.Edges.Add(new EdgeRec(toId, kind, LineOf(at), inferred, why, label));
        void ToType(ITypeSymbol? type, GraphEdgeKind kind, SyntaxNode at, string? label = null)
        {
            foreach (var t in Unwrap(type))
                if (model.PrimaryFile(t) is { } target && target != file) Add(target.Id, kind, at, label: label);
        }

        int seen = 0;
        foreach (var node in root.DescendantNodes())
        {
            if ((++seen & 255) == 0) cx.Ct.ThrowIfCancellationRequested();
            switch (node)
            {
                case TypeDeclarationSyntax td:
                {
                    // the other parts of a partial type: one wire from each part to the main file
                    if (td.Modifiers.Any(SyntaxKind.PartialKeyword) && sm.GetDeclaredSymbol(td, cx.Ct) is { } self
                        && model.PrimaryFile(self) is { } main && main != file)
                        Add(main.Id, GraphEdgeKind.References, td, label: "partial");
                    if (td.BaseList == null) break;
                    var declared = sm.GetDeclaredSymbol(td, cx.Ct);
                    foreach (var bt in td.BaseList.Types)
                    {
                        var t = sm.GetTypeInfo(bt.Type, cx.Ct).Type;
                        if (t is INamedTypeSymbol nt && nt.TypeKind != TypeKind.Error)
                        {
                            var kind = nt.TypeKind == TypeKind.Interface && declared?.TypeKind != TypeKind.Interface ? GraphEdgeKind.Implements : GraphEdgeKind.Inherits;
                            if (model.PrimaryFile(nt) is { } target && target != file) Add(target.Id, kind, bt);
                            // generic arguments of a base (ViewModelBase<Foo>, IHandler<Bar>) are references
                            foreach (var arg in nt.TypeArguments) ToType(arg, GraphEdgeKind.References, bt);
                        }
                        else Unbound(bt.Type, td is InterfaceDeclarationSyntax ? GraphEdgeKind.Inherits : null);
                    }
                    break;
                }
                case InvocationExpressionSyntax inv:
                {
                    string name = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        SimpleNameSyntax sn => sn.Identifier.Text,
                        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
                        _ => "",
                    };
                    if (DiMethods.Contains(name)) Registration(inv, name);
                    if (!methodNames.Contains(name)) break;
                    var info = sm.GetSymbolInfo(inv, cx.Ct);
                    var method = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
                    if (method != null && model.FileOf(method.ReducedFrom ?? method.OriginalDefinition) is { } target && target != file)
                        Add(target.Id, GraphEdgeKind.Calls, inv, label: method.ContainingType?.Name + "." + method.Name);
                    break;
                }
                case BaseObjectCreationExpressionSyntax oc:
                {
                    var t = sm.GetTypeInfo(oc, cx.Ct).Type;
                    if (t != null && t.TypeKind != TypeKind.Error) ToType(t, GraphEdgeKind.References, oc, label: "new " + t.Name);
                    else if (oc is ObjectCreationExpressionSyntax named) Unbound(named.Type, null);
                    break;
                }
                case AssignmentExpressionSyntax asg when asg.Left is IdentifierNameSyntax { Identifier.Text: "DataContext" } or MemberAccessExpressionSyntax { Name.Identifier.Text: "DataContext" }:
                    DataContext(asg);
                    break;
                case AttributeSyntax attr:
                {
                    string an = attr.Name is QualifiedNameSyntax q ? q.Right.Identifier.Text : attr.Name.ToString();
                    if (!typeNames.Contains(an) && !typeNames.Contains(an + "Attribute")) break;
                    if (sm.GetSymbolInfo(attr, cx.Ct).Symbol is IMethodSymbol ctor) ToType(ctor.ContainingType, GraphEdgeKind.References, attr);
                    break;
                }
                case SimpleNameSyntax sn when typeNames.Contains(sn.Identifier.Text) && !IsDeclarationName(sn) && sn.Parent is not AttributeSyntax:
                {
                    if (sn.Parent is BaseTypeSyntax || sn.Parent is QualifiedNameSyntax { Parent: BaseTypeSyntax }) break;   // done above
                    if (sn.Parent is ObjectCreationExpressionSyntax) break;
                    var info = sm.GetSymbolInfo(sn, cx.Ct);
                    var sym = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                    if (sym is IAliasSymbol alias) sym = alias.Target;
                    if (sym is ITypeSymbol ts) ToType(ts, GraphEdgeKind.References, sn);
                    else if (sym == null && info.CandidateSymbols.Length == 0 && !(sn.Parent is MemberAccessExpressionSyntax ma2 && ma2.Name == sn)
                             && sn.Parent is not MemberBindingExpressionSyntax)
                        Unbound(sn, null);   // (the name after a dot is a member, not a type)
                    break;
                }
            }
        }

        // A name the compiler could not bind: an edge only when exactly one project type has that name, marked inferred.
        void Unbound(TypeSyntax type, GraphEdgeKind? kind)
        {
            string simple = type switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                SimpleNameSyntax s => s.Identifier.Text,
                _ => "",
            };
            if (simple.Length == 0 || !model.BySimple.TryGetValue(simple, out var decls)) return;
            var targets = decls.Select(d => d.File).Distinct().ToList();
            if (targets.Count != 1 || targets[0] == file) return;
            Add(targets[0].Id, kind ?? (type.Parent is BaseTypeSyntax ? GraphEdgeKind.Inherits : GraphEdgeKind.References), type, inferred: true,
                why: $"'{simple}' did not resolve in this file (missing using, or declared in a project not read) — matched the only project type of that name");
        }

        // services.AddSingleton<IFoo, Foo>(), AddTransient(typeof(IFoo), typeof(Foo)), AddSingleton<IFoo>(sp => new Foo(…))
        void Registration(InvocationExpressionSyntax inv, string name)
        {
            var typeSyntaxes = new List<TypeSyntax>();
            var gn = inv.Expression switch
            {
                MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
                GenericNameSyntax g => g,
                _ => null,
            };
            if (gn != null) typeSyntaxes.AddRange(gn.TypeArgumentList.Arguments);
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                if (arg.Expression is TypeOfExpressionSyntax tof) typeSyntaxes.Add(tof.Type);
                foreach (var oc in arg.Expression.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>()) typeSyntaxes.Add(oc.Type);
            }
            foreach (var ts in typeSyntaxes)
                ToType(sm.GetTypeInfo(ts, cx.Ct).Type, GraphEdgeKind.Registers, inv, label: name);
        }

        void DataContext(AssignmentExpressionSyntax asg)
        {
            var vm = sm.GetTypeInfo(asg.Right, cx.Ct).Type;
            if (vm is IErrorTypeSymbol || vm == null || model.PrimaryFile(vm) == null)
            {
                // DataContext = provider.GetRequiredService<FooViewModel>() — the type argument says what it is
                if (asg.Right.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault() is { TypeArgumentList.Arguments.Count: 1 } g)
                    vm = sm.GetTypeInfo(g.TypeArgumentList.Arguments[0], cx.Ct).Type;
            }
            if (vm == null || model.PrimaryFile(vm) == null) return;
            INamedTypeSymbol? view = asg.Left is MemberAccessExpressionSyntax ma
                ? sm.GetTypeInfo(ma.Expression, cx.Ct).Type as INamedTypeSymbol
                : sm.GetEnclosingSymbol(asg.SpanStart, cx.Ct)?.ContainingType;
            if (view == null || view.TypeKind == TypeKind.Error) return;
            read.DataContexts.Add((CSharpModel.Name(view), CSharpModel.Name((INamedTypeSymbol)vm.OriginalDefinition), LineOf(asg)));
        }
    }

    private static bool IsDeclarationName(SimpleNameSyntax sn) => sn.Parent is NameColonSyntax or NameEqualsSyntax;

    /// <summary>Foo, Foo[], Foo?, List&lt;Foo&gt;, Task&lt;IReadOnlyList&lt;Foo&gt;&gt; → Foo (and List's other arguments).</summary>
    private static IEnumerable<ITypeSymbol> Unwrap(ITypeSymbol? type, int depth = 0)
    {
        if (type == null || depth > 4) yield break;
        switch (type)
        {
            case IArrayTypeSymbol a:
                foreach (var t in Unwrap(a.ElementType, depth + 1)) yield return t;
                break;
            case INamedTypeSymbol n:
                if (n.TypeKind != TypeKind.Error) yield return n.OriginalDefinition;
                foreach (var arg in n.TypeArguments)
                    foreach (var t in Unwrap(arg, depth + 1)) yield return t;
                break;
        }
    }
}

/// <summary>What the C# read leaves for the XAML read: the bound project types (by full name), their members, and
/// which view has which DataContext set in code.</summary>
internal sealed class CSharpModel
{
    private readonly CSharpCompilation _compilation;
    private readonly Dictionary<SyntaxTree, SourceFile> _fileOf;

    public CSharpModel(CSharpCompilation compilation, Dictionary<SyntaxTree, SourceFile> fileOf, Dictionary<string, List<(SourceFile File, int Line)>> bySimple)
    {
        _compilation = compilation;
        _fileOf = fileOf;
        BySimple = bySimple;
    }

    public Dictionary<string, List<(SourceFile File, int Line)>> BySimple { get; }

    /// <summary>How many files this build bound (the others' reads were kept from the last build).</summary>
    public int Rebound { get; set; }

    /// <summary>View type full name → (view model full name, where it is set).</summary>
    public ConcurrentDictionary<string, (string Vm, SourceFile File, int Line)> DataContexts { get; } = new(StringComparer.Ordinal);

    public static string Name(INamedTypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None));

    public SourceFile? FileOf(ISymbol s)
    {
        foreach (var r in s.DeclaringSyntaxReferences)
            if (_fileOf.TryGetValue(r.SyntaxTree, out var f)) return f;
        return null;
    }

    /// <summary>The file a type "is": its only declaration, or for a partial type the part named after it
    /// (ChatViewModel.cs, not ChatViewModel.Studio.cs).</summary>
    public SourceFile? PrimaryFile(ITypeSymbol t)
    {
        var files = t.DeclaringSyntaxReferences.Select(r => _fileOf.TryGetValue(r.SyntaxTree, out var f) ? f : null).Where(f => f != null).Distinct().ToList();
        if (files.Count <= 1) return files.FirstOrDefault();
        return files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f!.Full), t.Name, StringComparison.OrdinalIgnoreCase))
            ?? files.OrderBy(f => f!.Id.Length).First();
    }

    public INamedTypeSymbol? Type(string fullName)
    {
        var t = _compilation.GetTypeByMetadataName(fullName);
        return t != null && FileOf(t) != null ? t : null;
    }

    public SourceFile? PrimaryFile(string fullName) => Type(fullName) is { } t ? PrimaryFile(t) : null;

    /// <summary>The full name of the type called <paramref name="simple"/> that this file declares.</summary>
    public string? TypeIn(SourceFile file, string simple) =>
        _compilation.GetSymbolsWithName(simple, SymbolFilter.Type).OfType<INamedTypeSymbol>().Where(t => t.DeclaringSyntaxReferences.Any(r => _fileOf.TryGetValue(r.SyntaxTree, out var f) && f == file)).Select(Name).FirstOrDefault();

    public int LineOf(ISymbol s) =>
        s.Locations.FirstOrDefault(l => l.IsInSource) is { } l ? l.GetLineSpan().StartLinePosition.Line + 1 : 1;

    /// <summary>A property, field, method or event of the type or of a project base type: where it is declared.</summary>
    public (SourceFile File, ISymbol Member)? Member(string typeFullName, string member)
    {
        for (var t = Type(typeFullName); t != null; t = t.BaseType)
        {
            var m = t.GetMembers(member).FirstOrDefault(x => x is IPropertySymbol or IFieldSymbol or IMethodSymbol or IEventSymbol);
            if (m != null && FileOf(m) is { } f) return (f, m);
            if (t.BaseType != null && FileOf(t.BaseType) == null) break;   // past the project's own types
        }
        return null;
    }

    /// <summary>The type of a property (for a binding path a.b.c), when it is a project type.</summary>
    public string? MemberType(ISymbol m) => (m switch { IPropertySymbol p => p.Type, IFieldSymbol f => f.Type, _ => null }) is INamedTypeSymbol nt && FileOf(nt) != null ? Name(nt) : null;
}
