using System.Reflection;
using PdfSharp.Fonts;

namespace PaperMachine.Historian.Web;

internal sealed class EmbeddedReportFontResolver : IFontResolver
{
    public const string FamilyName = "Paper Machine Open Sans";
    private const string RegularFace = "PaperMachine.OpenSans.Regular";
    private const string BoldFace = "PaperMachine.OpenSans.Bold";
    private static readonly EmbeddedReportFontResolver Instance = new();
    private static readonly object RegistrationLock = new();
    private static bool _registered;
    private readonly byte[] _regular =
        ReadResource("PaperMachine.Reporting.OpenSans-Regular.ttf");
    private readonly byte[] _bold =
        ReadResource("PaperMachine.Reporting.OpenSans-Bold.ttf");

    public static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (_registered)
                return;
            GlobalFontSettings.FontResolver = Instance;
            _registered = true;
        }
    }

    public FontResolverInfo ResolveTypeface(
        string familyName,
        bool isBold,
        bool isItalic) =>
        new(isBold ? BoldFace : RegularFace, false, isItalic);

    public byte[] GetFont(string faceName) => faceName switch
    {
        BoldFace => _bold,
        _ => _regular
    };

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"O recurso incorporado de relatório '{name}' não foi encontrado.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
