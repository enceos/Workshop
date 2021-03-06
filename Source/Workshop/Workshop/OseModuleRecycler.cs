﻿namespace Workshop
{
    using System;
    using System.Linq;
    using System.Collections.Generic;

    using KIS;

    using UnityEngine;

    public class OseModuleRecycler : PartModule
    {
        private WorkshopItem _processedItem;
        private WorkshopItem _canceledItem;
        private WorkshopItem _addedItem;
        private int _addedItemKey;
        private ModuleKISInventory _addedItemInventory;

        private double _massProcessed;
        private float _progress;

        private readonly Clock _clock;
        private readonly WorkshopQueue _queue;

        // GUI Properties
        private Rect _windowPos;
        private Vector2 _scrollPosItems = Vector2.zero;
        private Vector2 _scrollPosQueue = Vector2.zero;
        private bool _showGui;

        [KSPField]
        public float ConversionRate = 0.25f;

        [KSPField]
        public float ProductivityFactor = 0.1f;

        [KSPField]
        public string OutputResource = "MaterialKits";

        [KSPField]
        public string UpkeepResource = "ElectricCharge";

        [KSPField]
        public int MinimumCrew = 2;

        [KSPField(guiName = "Recycler Status", guiActive = true)]
        public string Status = "Online";

        [KSPEvent(guiActive = true, guiName = "Open Recycler")]
        public void ContextMenuOnOpenWorkbench()
        {
            if (_showGui)
            {
                foreach (var inventory in part.vessel.FindPartModulesImplementing<ModuleKISInventory>().Where(i => i.showGui == false).ToList())
                {
                    foreach (var item in inventory.items)
                    {
                        item.Value.DisableIcon();
                    }
                    foreach (var item in _queue)
                    {
                        item.DisableIcon();
                    }
                    if (this._processedItem != null)
                    {
                        this._processedItem.DisableIcon();
                    }
                }
                _showGui = false;
            }
            else
            {
                _showGui = true;
            }
        }

        public OseModuleRecycler()
        {
            _clock = new Clock();
            _queue = new WorkshopQueue();
        }

        public override void OnStart(StartState state)
        {
            if (WorkshopSettings.IsKISAvailable)
            {
                GameEvents.onVesselChange.Add(this.OnVesselChange);
            }
            else
            {
                this.Fields["Status"].guiActive = false;
                this.Events["ContextMenuOnOpenWorkbench"].guiActive = false;
            }
            base.OnStart(state);
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            if (HighLogic.LoadedSceneIsFlight)
            {
                LoadModuleState(node);
            }
        }

        private void LoadModuleState(ConfigNode node)
        {
            foreach (ConfigNode cn in node.nodes)
            {
                if (cn.name == "BUILTPART" && cn.HasValue("Name") && cn.HasValue("MassProcessed"))
                {
                    var availablePart = PartLoader.getPartInfoByName(cn.GetValue("Name"));
                    if (availablePart != null)
                    {
                        this._processedItem = new WorkshopItem(availablePart);
                        _massProcessed = double.Parse(cn.GetValue("MassProcessed"));
                    }
                }
                if (cn.name == "QUEUEDPART" && cn.HasValue("Name"))
                {
                    var availablePart = PartLoader.getPartInfoByName(cn.GetValue("Name"));
                    var item = new WorkshopItem(availablePart);
                    _queue.Add(item);
                }
            }
        }

        public override void OnSave(ConfigNode node)
        {
            if (this._processedItem != null)
            {
                var builtPartNode = node.AddNode("BUILTPART");
                builtPartNode.AddValue("Name", this._processedItem.Part.name);
                builtPartNode.AddValue("MassProcessed", _massProcessed);
            }

            foreach (var queuedPart in _queue)
            {
                var queuedPartNode = node.AddNode("QUEUEDPART");
                queuedPartNode.AddValue("Name", queuedPart.Part.name);
            }

            base.OnSave(node);
        }

        public override void OnUpdate()
        {
            var deltaTime = _clock.GetDeltaTime();
            try
            {
                this.RemoveCanceledItemFromQueue();
                this.AddNewItemToQueue();
                this.ProcessItem(deltaTime);
            }
            catch (Exception ex)
            {
                Debug.LogError("[OSE] - OseModuleWorkshop_OnUpdate - " + ex.Message);
            }
            base.OnUpdate();
        }

        private void ProcessItem(double deltaTime)
        {
            if (_progress >= 100)
            {
                FinishManufacturing();
            }
            else if (this._processedItem != null)
            {
                ExecuteManufacturing(deltaTime);
            }
            else
            {
                StartManufacturing();
            }
        }

        private void RemoveCanceledItemFromQueue()
        {
            if (_canceledItem != null)
            {
                _canceledItem.DisableIcon();
                _queue.Remove(_canceledItem);
                _canceledItem = null;
            }
        }

        private void AddNewItemToQueue()
        {
            if (_addedItem == null)
            {
                return;
            }

            _queue.Add(_addedItem);
            _addedItemInventory.DeleteItem(_addedItemKey);
            _addedItem = null;
            _addedItemInventory = null; 
            _addedItemKey = -1;
        }

        private void StartManufacturing()
        {
            var nextQueuedPart = _queue.Pop();
            if (nextQueuedPart != null)
            {
                _processedItem = nextQueuedPart;
            }
        }

        private void ExecuteManufacturing(double deltaTime)
        {
            var preRequisitesMessage = CheckPrerequisites(deltaTime);

            if (preRequisitesMessage != "Ok")
            {
                Status = preRequisitesMessage;
            }
            else
            {
                Status = "Scrapping " + this._processedItem.Part.title;

                //Consume Upkeep
                this.RequestResource(this.UpkeepResource, deltaTime);

                //Produce Output
                var density = PartResourceLibrary.Instance.GetDefinition(this.OutputResource).density;
                var resourcesUsed = this.StoreResource(this.OutputResource, deltaTime * ProductivityFactor);
                _massProcessed += resourcesUsed * density;
            }

            this._progress = (float)(_massProcessed / (this._processedItem.Part.partPrefab.mass * this.ConversionRate) * 100);
        }

        public double AmountAvailable(string resource)
        {
            var res = PartResourceLibrary.Instance.GetDefinition(resource);
            var resList = new List<PartResource>();
            part.GetConnectedResources(res.id, res.resourceFlowMode, resList);
            return resList.Sum(r => r.amount);
        }

        public double StoreResource(string resource, double amount)
        {
            var res = PartResourceLibrary.Instance.GetDefinition(resource);
            var resList = new List<PartResource>();
            part.GetConnectedResources(res.id, res.resourceFlowMode, resList);
            var demandLeft = amount;
            var amountStored = 0d;

            foreach (var r in resList)
            {
                if (r.maxAmount - r.amount > demandLeft)
                {
                    r.amount += demandLeft;
                    amountStored += demandLeft;
                    demandLeft = 0;
                }
                else
                {
                    var amountToStore = r.maxAmount - r.amount;
                    r.amount += amountToStore;
                    demandLeft -= amountToStore;
                    amountStored += amountToStore;
                }
            }

            return amountStored;
        }

        public double RequestResource(string resource, double amount)
        {
            var res = PartResourceLibrary.Instance.GetDefinition(resource);
            var resList = new List<PartResource>();
            part.GetConnectedResources(res.id, res.resourceFlowMode, resList);
            var demandLeft = amount;
            var amountTaken = 0d;

            foreach (var r in resList)
            {
                if (r.amount >= demandLeft)
                {
                    amountTaken += demandLeft;
                    r.amount -= demandLeft;
                    demandLeft = 0;
                }
                else
                {
                    amountTaken += r.amount;
                    demandLeft -= r.amount;
                    r.amount = 0;
                }
            }

            return amountTaken;
        }

        private void FinishManufacturing()
        {
            _processedItem.DisableIcon();
            _processedItem = null;
            _massProcessed = 0;
            _progress = 0;
            Status = "Online";
        }

        public override void OnInactive()
        {
            if (_showGui)
            {
                ContextMenuOnOpenWorkbench();
            }
            base.OnInactive();
        }

        void OnVesselChange(Vessel v)
        {
            if (_showGui)
            {
                this.ContextMenuOnOpenWorkbench();
            }
        }

        private string CheckPrerequisites(double deltaTime)
        {
            if (this.part.protoModuleCrew.Count < MinimumCrew)
            {
                return "Not enough Crew to operate";
            }

            if (this.AmountAvailable(this.UpkeepResource) < deltaTime)
            {
                return "Not enough " + this.UpkeepResource;
            }

            return "Ok";
        }

        // ReSharper disable once UnusedMember.Local => Unity3D
        // ReSharper disable once InconsistentNaming => Unity3D
        void OnGUI()
        {
            if (_showGui)
            {
                DrawWindow();
            }
        }

        private void DrawWindow()
        {
            GUI.skin = HighLogic.Skin;
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.skin.button.alignment = TextAnchor.MiddleCenter;

            _windowPos = GUILayout.Window(
                   GetInstanceID(),
                   _windowPos,
                   DrawWindowContents,
                   "Recycler Menu",
                   GUILayout.ExpandWidth(true),
                   GUILayout.ExpandHeight(true),
                   GUILayout.MinWidth(64),
                   GUILayout.MinHeight(64));
        }

        private void DrawWindowContents(int windowId)
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            DrawAvailableItems();
            DrawQueuedItems();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            DrawBuiltItem();

            if (GUI.Button(new Rect(_windowPos.width - 24, 4, 20, 20), "X"))
            {
                ContextMenuOnOpenWorkbench();
            }

            GUI.DragWindow();
        }

        private void DrawAvailableItems()
        {
            GUILayout.BeginVertical();
            _scrollPosItems = GUILayout.BeginScrollView(_scrollPosItems, WorkshopStyles.Databox(), GUILayout.Width(400f), GUILayout.Height(250f));
            foreach (var inventory in part.vessel.FindPartModulesImplementing<ModuleKISInventory>())
            {
                foreach (var item in inventory.items)
                {
                    if (item.Value.icon == null)
                    {
                        item.Value.EnableIcon(128);
                    }
                    GUILayout.BeginHorizontal();
                    WorkshopGui.ItemThumbnail(item.Value.icon);
                    WorkshopGui.ItemDescription(item.Value.availablePart, this.OutputResource, this.ConversionRate);
                    if (GUILayout.Button("Queue", WorkshopStyles.Button(), GUILayout.Width(60f), GUILayout.Height(40f)))
                    {
                        _addedItem = new WorkshopItem(item.Value.availablePart);
                        _addedItemKey = item.Key;
                        _addedItemInventory = inventory;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawQueuedItems()
        {
            GUILayout.BeginVertical();
            _scrollPosQueue = GUILayout.BeginScrollView(_scrollPosQueue, WorkshopStyles.Databox(), GUILayout.Width(400f), GUILayout.Height(250f));
            foreach (var item in this._queue)
            {
                GUILayout.BeginHorizontal();
                if (item.Icon == null)
                {
                    item.EnableIcon(128);
                }
                WorkshopGui.ItemThumbnail(item.Icon);
                WorkshopGui.ItemDescription(item.Part, this.OutputResource, this.ConversionRate);
                if (GUILayout.Button("Remove", WorkshopStyles.Button(), GUILayout.Width(60f), GUILayout.Height(40f)))
                {
                    _canceledItem = item;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawBuiltItem()
        {
            GUILayout.BeginHorizontal();
            if (this._processedItem != null)
            {
                if (this._processedItem.Icon == null)
                {
                    this._processedItem.EnableIcon(128);
                }
                WorkshopGui.ItemThumbnail(this._processedItem.Icon);
            }
            else
            {
                GUILayout.Box("", GUILayout.Width(50), GUILayout.Height(50));
            }
            WorkshopGui.ProgressBar(_progress);
            GUILayout.EndHorizontal();
        }

        public override string GetInfo()
        {
            return "Recycler Description for TechTree";
        }
    }
}
