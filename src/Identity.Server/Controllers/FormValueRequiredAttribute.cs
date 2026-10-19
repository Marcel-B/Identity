using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Identity.Server.Controllers;

/// <summary>Selects an action only when the posted form contains the given field, e.g. the name of the pressed button.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FormValueRequiredAttribute(string name) : ActionMethodSelectorAttribute
{
    public override bool IsValidForRequest(RouteContext context, ActionDescriptor action)
    {
        var request = context.HttpContext.Request;
        return HttpMethods.IsPost(request.Method) &&
               request.HasFormContentType &&
               !string.IsNullOrEmpty(request.Form[name]);
    }
}
