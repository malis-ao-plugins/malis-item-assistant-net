using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

public record struct ItemInfo(int Id, int HighId, int Quality, int Slot, Identity UniqueIdentity, Identity ParentContainer);
public class ItemTrack
{
    private static Dictionary<int, int> _handleToIdentity = new();
    private static InventoryDb? _inventoryDb;

    public static void Start(InventoryDb repo)
    {
        _inventoryDb = repo;
        _inventoryDb.UpsertCharacter(DynelManager.LocalPlayer.Identity.Instance, DynelManager.LocalPlayer.Name);

        UIController.ItemAdded += ItemAdded;
        UIController.ItemRemoved += ItemRemoved;
        Network.N3MessageReceived += OnN3MessageReceived;

        foreach (var bag in Inventory.Backpacks.Where(x => x.IsOpen && x.Items.Count > 0))
            _handleToIdentity[(bag.Items.FirstOrDefault()!.Slot.Instance >> 16) & 0xFFFF] = bag.Identity.Instance;

        foreach (var item in Inventory.Items)
            ItemAdded(item.Slot);

        if (Inventory.Bank.IsOpen)
            foreach (var item in Inventory.Bank.Items)
                ItemAdded(item.Slot);
    }

    public static void Stop()
    {
        UIController.ItemAdded -= ItemAdded;
        UIController.ItemRemoved -= ItemRemoved;
        Network.N3MessageReceived -= OnN3MessageReceived;
    }

    private static void OnN3MessageReceived(object? sender, N3Message e)
    {
        if (e is InventoryUpdateMessage msg)
            _handleToIdentity[msg.Handle] = msg.InventoryIdentity.Instance;
    }

    private static void ItemAdded(Identity itemSlot)
    {
        if (_inventoryDb == null)
            return;

        if (!TryGetItem(itemSlot, out ItemInfo item))
            return;

        _inventoryDb.ItemAdded(DynelManager.LocalPlayer.Identity.Instance, item);
    }

    private static void ItemRemoved(Identity slotIdentity)
    {
        _inventoryDb?.ItemRemoved(DynelManager.LocalPlayer.Identity.Instance, slotIdentity, _handleToIdentity);
    }

    private static bool TryGetItem(Identity slot, out ItemInfo itemInfo)
    {
        itemInfo = new ItemInfo();

        if (slot.Type == IdentityType.BankByRef)
        {
            var item = Inventory.Bank.Items.FirstOrDefault(x => x.Slot.Instance == slot.Instance)!;
            itemInfo = new ItemInfo(item.Id, item.HighId, item.QualityLevel, item.Slot.Instance, item.UniqueIdentity, new Identity(IdentityType.Bank, DynelManager.LocalPlayer.Identity.Instance));
            return true;
        }

        if (slot.Type == IdentityType.Backpack)
        {
            bool matchFound = false;

            foreach (var bag in Inventory.Backpacks.Where(x => x.IsOpen))
            {
                if (matchFound)
                    break;

                foreach (var bagItem in bag.Items)
                {
                    if (bagItem.Slot == slot)
                    {
                        matchFound = true;
                        var item = Inventory.Backpacks.Where(x => x.IsOpen).SelectMany(x => x.Items).FirstOrDefault(x => x.Slot.Instance == slot.Instance)!;
                        int itemHandle = (item.Slot.Instance >> 16) & 0xFFFF;
                        int itemSlot = item.Slot.Instance & 0xFFFF;
                        itemInfo = new ItemInfo(item.Id, item.HighId, item.QualityLevel, itemSlot, item.UniqueIdentity, new Identity(IdentityType.Backpack, _handleToIdentity[itemHandle]));
                        break;
                    }
                }
            }
            return true;
        }

        if (slot.Type == IdentityType.Inventory)
        {
            var item = Inventory.Items.FirstOrDefault(x => x.Slot.Instance == slot.Instance)!;
            itemInfo = new ItemInfo(item.Id, item.HighId, item.QualityLevel, item.Slot.Instance, item.UniqueIdentity, new Identity(IdentityType.Inventory, DynelManager.LocalPlayer.Identity.Instance));
            return true;
        }

        return false;
    }
}