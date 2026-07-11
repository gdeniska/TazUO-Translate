// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.Gumps.GridHighLight;
using ClassicUO.Network;
using ClassicUO.Network.PacketHandlers.Helpers;
using ClassicUO.Utility;

namespace ClassicUO.Game.Managers
{
    public sealed class ObjectPropertiesListManager
    {
        private readonly Dictionary<uint, ItemProperty> _itemsProperties = new Dictionary<uint, ItemProperty>();
        private readonly Dictionary<uint, string> _entityNameOriginals = new Dictionary<uint, string>();
        private World _world;

        public ObjectPropertiesListManager(World world)
        {
            _world = world;
            LocalTranslationService.Instance.CacheInvalidated += RestoreCachedTranslations;
            LocalTranslationService.Instance.TranslationDisplayDisabled += RestoreCachedTranslations;
        }

        public void Add(uint serial, uint revision, string name, string data, int namecliloc)
        {
            string originalName = name;
            string originalData = data;
            bool translateNames = ProfileManager.CurrentProfile?.LocalTranslationEnabled == true
                                  && ProfileManager.CurrentProfile.LocalTranslationItemNames;
            bool translateProperties = ProfileManager.CurrentProfile?.LocalTranslationEnabled == true
                                       && ProfileManager.CurrentProfile.LocalTranslationItemProperties;

            if (translateNames)
            {
                if (LocalTranslationService.Instance.TryGetCached(name, TranslationScenario.ItemName, out string cachedName))
                    name = cachedName;
            }

            if (translateProperties)
            {
                if (LocalTranslationService.Instance.TryGetCached(data, TranslationScenario.ItemProperty, out string cachedData))
                    data = cachedData;
            }

            if (!_itemsProperties.TryGetValue(serial, out ItemProperty prop))
            {
                prop = new ItemProperty();
                _itemsProperties[serial] = prop;
            }

            prop.Serial = serial;
            prop.Revision = revision;
            prop.Name = name;
            prop.Data = data;
            prop.OriginalName = originalName;
            prop.OriginalData = originalData;
            prop.NameCliloc = namecliloc;

            EventSink.InvokeOPLOnReceive(null, new OPLEventArgs(serial, name, data));
            RefreshNameOverhead(serial);

            Item item = _world.Items.Get(serial);
            if(item != null)
                ItemDatabaseManager.Instance.AddOrUpdateItem(item, _world);

            if (translateNames)
            {
                if (string.Equals(originalName, name, StringComparison.Ordinal)
                    && LocalTranslationService.ShouldTranslate(originalName))
                    _ = TranslatePropertyPartAsync(serial, revision, originalName, name, true, LocalTranslationService.Instance.CacheGeneration);
            }

            if (translateProperties)
            {
                if (string.Equals(originalData, data, StringComparison.Ordinal)
                    && LocalTranslationService.ShouldTranslate(originalData))
                    _ = TranslatePropertyPartAsync(serial, revision, originalData, data, false, LocalTranslationService.Instance.CacheGeneration);
            }
        }

        private async Task TranslatePropertyPartAsync(
            uint serial,
            uint revision,
            string source,
            string expectedCurrent,
            bool isName,
            long cacheGeneration
        )
        {
            TranslationScenario scenario = isName ? TranslationScenario.ItemName : TranslationScenario.ItemProperty;
            string translated = await LocalTranslationService.Instance.TranslateAsync(source, scenario).ConfigureAwait(false);
            if (string.Equals(source, translated, StringComparison.Ordinal))
                return;

            MainThreadQueue.EnqueueAction(() =>
            {
                if (cacheGeneration != LocalTranslationService.Instance.CacheGeneration)
                    return;

                Profile profile = ProfileManager.CurrentProfile;
                if (profile?.LocalTranslationEnabled != true
                    || (isName ? !profile.LocalTranslationItemNames : !profile.LocalTranslationItemProperties))
                    return;

                if (!_itemsProperties.TryGetValue(serial, out ItemProperty current) || current.Revision != revision)
                    return;

                string currentValue = isName ? current.Name : current.Data;
                if (!string.Equals(currentValue, expectedCurrent, StringComparison.Ordinal))
                    return;

                if (isName)
                {
                    current.Name = translated;
                    Entity entity = _world.Get(serial);
                    if (entity != null && string.Equals(entity.Name, source, StringComparison.Ordinal))
                        entity.Name = translated;

                    // Object-handle gumps keep their own rendered text.  OPL data can arrive
                    // before its asynchronous translation, so explicitly refresh an already
                    // visible handle once the translated name replaces the original.
                    RefreshNameOverhead(serial);
                }
                else
                    current.Data = translated;

                EventSink.InvokeOPLOnReceive(null, new OPLEventArgs(serial, current.Name, current.Data));
                Item item = _world.Items.Get(serial);
                if (item != null)
                    ItemDatabaseManager.Instance.AddOrUpdateItem(item, _world);
            });
        }

        public bool Contains(uint serial)
        {
            if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.ForceTooltipsOnOldClients)
                ForcedTooltipManager.RequestName(_world, serial);

            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
                return true; //p.Revision != 0;  <-- revision == 0 can contain the name.

            // if we don't have the OPL of this item, let's request it to the server.
            // Original client seems asking for OPL when character is not running.
            // We'll ask OPL when mouse is over an object.
            SharedStore.AddMegaCliLocRequest(serial);

            return false;
        }

        /// <summary>
        /// Applies the legacy UpdateName packet for an item. These names are displayed above an object after
        /// a click and do not travel through the object's property list.
        /// </summary>
        public void SetEntityName(Item item, string name)
        {
            if (item == null)
                return;

            item.Name = name;
            _entityNameOriginals[item.Serial] = name;

            bool translate = ProfileManager.CurrentProfile?.LocalTranslationEnabled == true
                             && ProfileManager.CurrentProfile.LocalTranslationItemNames;
            if (!translate || !LocalTranslationService.ShouldTranslate(name))
            {
                RefreshNameOverhead(item.Serial);
                return;
            }

            if (LocalTranslationService.Instance.TryGetCached(name, TranslationScenario.ItemName, out string cached))
            {
                item.Name = cached;
                RefreshNameOverhead(item.Serial);
                return;
            }

            _ = TranslateEntityNameAsync(item.Serial, name, LocalTranslationService.Instance.CacheGeneration);
            RefreshNameOverhead(item.Serial);
        }

        private async Task TranslateEntityNameAsync(uint serial, string original, long cacheGeneration)
        {
            string translated = await LocalTranslationService.Instance.TranslateAsync(original, TranslationScenario.ItemName).ConfigureAwait(false);
            if (string.Equals(original, translated, StringComparison.Ordinal))
                return;

            MainThreadQueue.EnqueueAction(() =>
            {
                if (cacheGeneration != LocalTranslationService.Instance.CacheGeneration
                    || ProfileManager.CurrentProfile?.LocalTranslationEnabled != true
                    || !ProfileManager.CurrentProfile.LocalTranslationItemNames
                    || !_entityNameOriginals.TryGetValue(serial, out string currentOriginal)
                    || !string.Equals(currentOriginal, original, StringComparison.Ordinal)
                    || _world.Get(serial) is not Item item
                    || !string.Equals(item.Name, original, StringComparison.Ordinal))
                    return;

                item.Name = translated;
                RefreshNameOverhead(serial);
            });
        }

        public bool IsRevisionEquals(uint serial, uint revision)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty prop))
            {
                return (revision & ~0x40000000) == prop.Revision || // remove the mask
                       revision == prop.Revision;                   // if mask removing didn't work, try a simple compare.
            }

            return false;
        }

        public bool TryGetRevision(uint serial, out uint revision)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                revision = p.Revision;

                return true;
            }

            revision = 0;

            return false;
        }

        public bool TryGetNameAndData(uint serial, out string name, out string data)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                name = p.Name;
                data = p.Data;

                return true;
            }

            name = data = null;

            return false;
        }

        public int GetNameCliloc(uint serial)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                return p.NameCliloc;
            }

            return 0;
        }

        public ItemPropertiesData TryGetItemPropertiesData(World world, uint serial)
        {
            if (Contains(serial))
                if (world.Items.TryGetValue(serial, out Item item))
                    return new ItemPropertiesData(world, item);
            return null;
        }

        public void Remove(uint serial) => _itemsProperties.Remove(serial);

        public void Clear()
        {
            _itemsProperties.Clear();
            _entityNameOriginals.Clear();
        }

        private void RestoreCachedTranslations(IReadOnlyCollection<TranslationScenario> scenarios)
        {
            bool restoreNames = scenarios.Count == 0 || scenarios.Contains(TranslationScenario.ItemName);
            bool restoreProperties = scenarios.Count == 0 || scenarios.Contains(TranslationScenario.ItemProperty);

            if (!restoreNames && !restoreProperties)
                return;

            MainThreadQueue.EnqueueAction(() =>
            {
                foreach (ItemProperty property in _itemsProperties.Values)
                {
                    bool changed = false;

                    if (restoreNames && property.OriginalName != null && !string.Equals(property.Name, property.OriginalName, StringComparison.Ordinal))
                    {
                        string translatedName = property.Name;
                        property.Name = property.OriginalName;
                        Entity entity = _world.Get(property.Serial);
                        if (entity != null && string.Equals(entity.Name, translatedName, StringComparison.Ordinal))
                            entity.Name = property.OriginalName;
                        changed = true;
                    }

                    if (restoreProperties && property.OriginalData != null && !string.Equals(property.Data, property.OriginalData, StringComparison.Ordinal))
                    {
                        property.Data = property.OriginalData;
                        changed = true;
                    }

                    if (!changed)
                        continue;

                    EventSink.InvokeOPLOnReceive(null, new OPLEventArgs(property.Serial, property.Name, property.Data));
                    Item item = _world.Items.Get(property.Serial);
                    if (item != null)
                        ItemDatabaseManager.Instance.AddOrUpdateItem(item, _world);
                }

                if (restoreNames)
                {
                    foreach (KeyValuePair<uint, string> entry in _entityNameOriginals)
                    {
                        if (_world.Get(entry.Key) is Item item && !string.Equals(item.Name, entry.Value, StringComparison.Ordinal))
                        {
                            item.Name = entry.Value;
                            RefreshNameOverhead(entry.Key);
                        }
                    }
                }
            });
        }

        private static void RefreshNameOverhead(uint serial) => UIManager.GetGump<NameOverheadGump>(serial)?.SetName();

    }

    public class ItemProperty
    {
        public bool IsEmpty => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Data);
        public string Data;
        public string Name;
        public uint Revision;
        public uint Serial;
        public int NameCliloc;
        public string OriginalData;
        public string OriginalName;

        public string CreateData(bool extended) => string.Empty;
    }

    public class ItemPropertiesData
    {
        public readonly bool HasData = false;
        public string Name = "";
        public readonly string RawData = "";
        public readonly uint serial;
        public string[] RawLines;
        public readonly Item item, itemComparedTo;
        public List<SinglePropertyData> singlePropertyData = new List<SinglePropertyData>();

        private World world;

        public ItemPropertiesData(World world, Item item, Item compareTo = null)
        {
            if (item == null)
                return;
            this.world = world;
            this.item = item;
            itemComparedTo = compareTo;

            serial = item.Serial;
            if (world.OPL.TryGetNameAndData(item.Serial, out Name, out RawData))
            {
                Name = Name.Trim();
                HasData = true;
                processData();
            }
        }

        public ItemPropertiesData(string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip))
                return;
            if (tooltip.Contains("\n"))
            {
                Name = tooltip.Substring(0, tooltip.IndexOf("\n"));
                RawData = tooltip.Substring(tooltip.IndexOf("\n") + 1);
            }
            else
            {
                Name = tooltip;
            }
            HasData = true;
            processData();
        }

        private void processData()
        {
            string formattedData = TextBox.ConvertHtmlToFontStashSharpCommand(RawData);

            RawLines = formattedData.Split(new string[] { "\n", "<br>" }, StringSplitOptions.None);

            foreach (string line in RawLines)
            {
                singlePropertyData.Add(new SinglePropertyData(line));
            }

            if (itemComparedTo != null)
            {
                GenComparisonData();
            }
        }

        private void GenComparisonData()
        {
            if (itemComparedTo == null) return;

            var itemPropertiesData = new ItemPropertiesData(world, itemComparedTo);
            if (itemPropertiesData.HasData)
            {
                foreach (SinglePropertyData thisItem in singlePropertyData)
                {
                    foreach (SinglePropertyData secondItem in itemPropertiesData.singlePropertyData)
                    {
                        if (String.Equals(thisItem.Name, secondItem.Name, StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (thisItem.FirstValue != double.MinValue && secondItem.FirstValue != double.MinValue)
                            {
                                thisItem.FirstDiff = thisItem.FirstValue - secondItem.FirstValue;
                            }

                            if (thisItem.SecondValue > double.MinValue && secondItem.SecondValue > double.MinValue)
                            {
                                thisItem.SecondDiff = thisItem.SecondValue - secondItem.SecondValue;
                            }
                            break;
                        }
                    }
                }
            }
        }

        public bool GenerateComparisonTooltip(ItemPropertiesData comparedTo, out string compiledToolTip)
        {
            if (!HasData)
            {
                compiledToolTip = null;
                return false;
            }

            string finalTooltip = Name + "\n";

            foreach (SinglePropertyData thisItem in singlePropertyData)
            {
                bool foundMatch = false;
                foreach (SinglePropertyData secondItem in comparedTo.singlePropertyData)
                {
                    if (string.Equals(thisItem.Name, secondItem.Name, StringComparison.InvariantCultureIgnoreCase))
                    {
                        foundMatch = true;
                        finalTooltip += thisItem.Name;

                        if (thisItem.FirstValue != double.MinValue && secondItem.FirstValue != double.MinValue)
                        {
                            double diff = thisItem.FirstValue - secondItem.FirstValue;
                            finalTooltip += $" {thisItem.FirstValue}";
                            if (diff != 0)
                            {
                                finalTooltip += $"({(diff >= 0 ? "/c[green]+" : "/c[red]")} {diff}/cd)";
                            }
                        }

                        if (thisItem.SecondValue > double.MinValue && secondItem.SecondValue > double.MinValue)
                        {
                            double diff = thisItem.SecondValue - secondItem.SecondValue;
                            finalTooltip += $" {thisItem.SecondValue}";
                            if (diff != 0)
                            {
                                finalTooltip += $"({(diff >= 0 ? "/c[green]+" : "/c[red]")}{diff}/cd)";
                            }
                        }

                        finalTooltip += "\n";
                        break;
                    }
                }
                if (!foundMatch)
                    finalTooltip += thisItem.ToString() + "\n";
            }

            compiledToolTip = finalTooltip;
            return true;
        }

        public string CompileTooltip()
        {
            string result = "";

            result += Name + "\n";
            foreach (SinglePropertyData data in singlePropertyData)
                result += $"{data.Name} [{data.FirstValue}] [{data.SecondValue}]\n";

            return result;
        }

        public class SinglePropertyData
        {
            public string OriginalString;
            public string Name = "";
            public double FirstValue = double.MinValue;
            public double SecondValue = double.MinValue;
            public double FirstDiff = 0;
            public double SecondDiff = 0;

            public SinglePropertyData(string line)
            {
                OriginalString = line;

                // Remove any color tags like /c[#...]
                string cleaned = RegexHelper.GetRegex(@"/c\[[#a-zA-Z0-9]+\]", RegexOptions.IgnoreCase).Replace(line, "").Replace("/cd", "").Trim();

                // Extract numbers
                MatchCollection matches = RegexHelper.GetRegex(@"-?\d+(\.\d+)?").Matches(cleaned);

                if (matches.Count > 0)
                {
                    double.TryParse(matches[0].Value, out FirstValue);
                    if (matches.Count > 1)
                        double.TryParse(matches[1].Value, out SecondValue);
                }

                // Remove all numbers and symbols from the cleaned string to isolate the name
                Name = RegexHelper.GetRegex(@"[-+]?\d+(\.\d+)?[%]?([- ]*\d+)?", RegexOptions.IgnoreCase).Replace(cleaned, "").Trim();

                // Fallback if something went wrong
                if (string.IsNullOrWhiteSpace(Name))
                    Name = line;
            }

            public override string ToString()
            {
                string output = "";

                if (Name != null)
                    output += Name;

                if (FirstValue != double.MinValue)
                    output += $" {FirstValue}";

                if (SecondValue != double.MinValue)
                    output += $" {SecondValue}";

                return output;
            }
        }
    }
}
