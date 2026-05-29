namespace SapTextureTool.Models;

// Per-sprite border widths. Top values apply at the silhouette's top edge, Bot values at the
// bottom edge, with linear interpolation across the silhouette's vertical bounding box.
// Order around the silhouette from inside-out: body → black ring → white ring → transparent.
public record BorderConfig(int BlackPxTop, int BlackPxBot, int WhitePxTop, int WhitePxBot);
