using System.Diagnostics;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Server.Pages;

[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public string Title { get; private set; } = "Etwas ist schiefgelaufen";

    public string Description { get; private set; } = "Bitte versuche es später noch einmal.";

    public string? RequestId { get; private set; }

    public void OnGet(int? code) => Load(code);

    public void OnPost(int? code) => Load(code);

    private void Load(int? code)
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        // Errors OpenIddict cannot send back to the client (e.g. an unknown client_id or redirect_uri).
        if (HttpContext.GetOpenIddictServerResponse() is { } response)
        {
            Title = "Ungültige Anmeldeanfrage";
            Description = $"{response.Error}: {response.ErrorDescription}";
            return;
        }

        if (code == 404)
        {
            Title = "Seite nicht gefunden";
            Description = "Diese Adresse gibt es nicht.";
        }
    }
}
