namespace JulOS.Frontend.Folders;

internal sealed record FolderDto(Guid Id, string Name, string Icon, int Sort);
/// <summary>Wo eine Verbindung einsortiert ist; die Oberflaeche legt es auf ihre Verbindungsliste (gleiche Kennung).</summary>
internal sealed record PlacementDto(Guid Id, Guid? FolderId, bool Favorite, DateTimeOffset? LastUsedAt, int UseCount);
internal sealed record OrganisationResponse(FolderDto[] Folders, PlacementDto[] Placements);
internal sealed record FolderRequest(string? Name, string? Icon);
internal sealed record FolderOrderRequest(Guid[]? Ids);
internal sealed record FolderAssignRequest(Guid? FolderId);
internal sealed record FavoriteRequest(bool? Favorite);
internal sealed record MoveRequest(int? Delta);
/// <summary>Was ein Geraet bisher nur lokal kannte: Favoriten, Zaehler je Verbindung, letzte Benutzung (Millisekunden seit 1970).</summary>
internal sealed record LocalImportRequest(Guid[]? Favorites, Dictionary<string, int>? Uses, Dictionary<string, long>? Last);
