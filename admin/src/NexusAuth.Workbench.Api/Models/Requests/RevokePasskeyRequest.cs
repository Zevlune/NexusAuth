using System.ComponentModel.DataAnnotations;

namespace NexusAuth.Workbench.Api.Models.Requests;

public sealed record RevokePasskeyRequest([Required, StringLength(500)] string Reason);
