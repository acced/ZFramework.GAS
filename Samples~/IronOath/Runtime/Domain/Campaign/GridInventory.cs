using System;

namespace IronOath.Campaign
{
    /// <summary>A fixed 6×5 bag. Failed placement and movement never alter items or occupancy.</summary>
    public sealed class GridInventory
    {
        public const int Columns = 6;
        public const int Rows = 5;
        public const int Capacity = Columns * Rows;
        private readonly InventoryEntry[] _items = new InventoryEntry[Capacity];
        private readonly int[] _cells = new int[Capacity];
        public int Count { get; private set; }
        public int OccupiedCells { get; private set; }
        public InventoryEntry GetAt(int index) => index >= 0 && index < Count ? _items[index] : null;
        public int GetCell(int x, int y) => x >= 0 && x < Columns && y >= 0 && y < Rows ? _cells[y * Columns + x] : -1;
        public InventoryEntry Find(int instanceId)
        {
            for (int i = 0; i < Count; i++) if (_items[i].InstanceId == instanceId) return _items[i];
            return null;
        }
        public bool TryPlace(InventoryEntry item, int x, int y, bool rotated = false)
        {
            if (item == null || item.Owner != null || Count == Capacity || Find(item.InstanceId) != null) return false;
            int width = rotated ? item.Definition.Height : item.Definition.Width;
            int height = rotated ? item.Definition.Width : item.Definition.Height;
            if (!CanPlace(_cells, x, y, width, height, 0)) return false;
            item.X = x; item.Y = y; item.Rotated = rotated; item.Owner = this;
            _items[Count++] = item; Fill(item, item.InstanceId); OccupiedCells += width * height;
            return true;
        }
        public bool TryAdd(InventoryEntry item)
        {
            if (item == null || item.Owner != null) return false;
            for (int rotation = 0; rotation < 2; rotation++)
                for (int y = 0; y < Rows; y++)
                    for (int x = 0; x < Columns; x++)
                        if (TryPlace(item, x, y, rotation == 1)) return true;
            return false;
        }
        public bool TryMove(int instanceId, int x, int y, bool rotated)
        {
            InventoryEntry item = Find(instanceId);
            if (item == null) return false;
            int width = rotated ? item.Definition.Height : item.Definition.Width;
            int height = rotated ? item.Definition.Width : item.Definition.Height;
            if (!CanPlace(_cells, x, y, width, height, instanceId)) return false;
            Fill(item, 0); item.X = x; item.Y = y; item.Rotated = rotated; Fill(item, instanceId);
            return true;
        }
        public bool TryRemove(int instanceId, out InventoryEntry item)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_items[i].InstanceId != instanceId) continue;
                item = _items[i]; Fill(item, 0); OccupiedCells -= item.Width * item.Height; item.Owner = null;
                for (int j = i; j < Count - 1; j++) _items[j] = _items[j + 1];
                _items[--Count] = null; return true;
            }
            item = null; return false;
        }
        internal bool CanFit(LootAward[] loot)
        {
            if (loot == null || Count + loot.Length > Capacity) return false;
            // Small stack scratch keeps reward preflight atomic and independent of current bag entries.
            Span<int> cells = stackalloc int[Capacity];
            for (int i = 0; i < Capacity; i++) cells[i] = _cells[i];
            for (int i = 0; i < loot.Length; i++)
            {
                ItemDefinition definition = ItemCatalog.Get(loot[i].Kind);
                bool placed = false;
                for (int rotation = 0; rotation < 2 && !placed; rotation++)
                {
                    int width = rotation == 0 ? definition.Width : definition.Height;
                    int height = rotation == 0 ? definition.Height : definition.Width;
                    for (int y = 0; y < Rows && !placed; y++)
                        for (int x = 0; x < Columns && !placed; x++)
                        {
                            if (!CanPlace(cells, x, y, width, height, 0)) continue;
                            for (int yy = y; yy < y + height; yy++)
                                for (int xx = x; xx < x + width; xx++) cells[yy * Columns + xx] = -1;
                            placed = true;
                        }
                }
                if (!placed) return false;
            }
            return true;
        }
        private static bool CanPlace(ReadOnlySpan<int> cells, int x, int y, int width, int height, int ignore)
        {
            if (x < 0 || y < 0 || x > Columns - width || y > Rows - height) return false;
            for (int yy = y; yy < y + height; yy++)
                for (int xx = x; xx < x + width; xx++)
                {
                    int occupant = cells[yy * Columns + xx];
                    if (occupant != 0 && occupant != ignore) return false;
                }
            return true;
        }
        private void Fill(InventoryEntry item, int value)
        {
            for (int y = item.Y; y < item.Y + item.Height; y++)
                for (int x = item.X; x < item.X + item.Width; x++) _cells[y * Columns + x] = value;
        }
    }
}
