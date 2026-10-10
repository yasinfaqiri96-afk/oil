using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PTGOilSystem.Web.Data;

/// <summary>
/// One row of a large single-transaction group operation. Every save scans every tracked
/// entity, so keeping all earlier rows' documents tracked made a 1,000-row group quadratic.
/// <see cref="ReleaseSaved"/> forgets only the entities this row added and already saved:
/// the rows stay written inside the caller's open transaction, while pending changes and
/// entities loaded by queries remain tracked.
/// </summary>
internal sealed class SavedRowTrackingScope : IDisposable
{
    private readonly DbContext _db;
    private readonly List<object> _added = [];

    public SavedRowTrackingScope(DbContext db)
    {
        _db = db;
        _db.ChangeTracker.Tracked += OnTracked;
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs e)
    {
        if (!e.FromQuery)
            _added.Add(e.Entry.Entity);
    }

    public void ReleaseSaved()
    {
        foreach (var entity in _added)
        {
            var entry = _db.Entry(entity);
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Detached;
        }
        _added.Clear();
    }

    public void Dispose() => _db.ChangeTracker.Tracked -= OnTracked;
}
