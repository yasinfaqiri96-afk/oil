using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PTGOilSystem.Web.Data;

/// <summary>
/// One row of a large single-transaction group operation. Every save scans every tracked
/// entity, so keeping all earlier rows' documents tracked made a 1,000-row group quadratic.
/// <see cref="ReleaseSaved"/> forgets only the entities that started being tracked during
/// this row and are already saved: the rows stay written inside the caller's open
/// transaction, while pending changes and entities tracked before the row stay tracked.
/// By default only entities the row added are released; <c>includeLoaded</c> also releases
/// rows the row loaded, for loops whose rows never share loaded documents.
/// </summary>
internal sealed class SavedRowTrackingScope : IDisposable
{
    private readonly DbContext _db;
    private readonly bool _includeLoaded;
    private readonly List<object> _tracked = [];

    public SavedRowTrackingScope(DbContext db, bool includeLoaded = false)
    {
        _db = db;
        _includeLoaded = includeLoaded;
        _db.ChangeTracker.Tracked += OnTracked;
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs e)
    {
        if (_includeLoaded || !e.FromQuery)
            _tracked.Add(e.Entry.Entity);
    }

    public void ReleaseSaved()
    {
        foreach (var entity in _tracked)
        {
            var entry = _db.Entry(entity);
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Detached;
        }
        _tracked.Clear();
    }

    public void Dispose() => _db.ChangeTracker.Tracked -= OnTracked;
}
