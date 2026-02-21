using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor.Services;

namespace UnityMCP.Editor.UI.Tabs
{
    /// <summary>
    /// Checkpoints tab: save, restore, and diff scene checkpoints.
    /// </summary>
    public class CheckpointsTab : ITab
    {
        public VisualElement Root { get; }

        private readonly TextField _nameField;
        private readonly ScrollView _listScrollView;
        private readonly VisualElement _listContainer;
        private readonly VisualElement _emptyState;

        private List<CheckpointMetadata> _cachedCheckpoints;

        public CheckpointsTab()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            // Toolbar row: name field + save button
            VisualElement toolbar = new VisualElement();
            toolbar.AddToClassList("row");
            toolbar.style.marginBottom = 8;

            _nameField = new TextField();
            _nameField.style.flexGrow = 1;
            _nameField.style.marginRight = 4;
            toolbar.Add(_nameField);

            // Set placeholder
            SetPlaceholder(_nameField, "Checkpoint name (optional)");

            Button saveButton = new Button(OnSave) { text = "Save Checkpoint" };
            saveButton.AddToClassList("button--accent");
            toolbar.Add(saveButton);

            Root.Add(toolbar);

            // List scroll view
            _listScrollView = new ScrollView(ScrollViewMode.Vertical);
            _listScrollView.AddToClassList("scroll-view");
            Root.Add(_listScrollView);

            _listContainer = _listScrollView.contentContainer;

            // Empty state
            _emptyState = new VisualElement();
            _emptyState.AddToClassList("empty-state");
            Label emptyLabel = new Label("No checkpoints saved.\nUse the button above or call scene_checkpoint from an AI client.");
            emptyLabel.AddToClassList("empty-state-text");
            _emptyState.Add(emptyLabel);
        }

        public void OnActivate()
        {
            RebuildList();
        }

        public void OnDeactivate() { }

        public void Refresh() { }

        private void OnSave()
        {
            string checkpointName = string.IsNullOrWhiteSpace(_nameField.value) ? null : _nameField.value.Trim();

            try
            {
                CheckpointMetadata metadata = CheckpointManager.SaveCheckpoint(checkpointName);
                if (metadata != null)
                {
                    _nameField.value = "";
                    RebuildList();
                }
                else
                {
                    Debug.LogWarning("[MCPServerWindow] Failed to save checkpoint. Ensure the scene is saved.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[MCPServerWindow] Error saving checkpoint: {exception.Message}");
            }
        }

        #region List Building

        private void RebuildList()
        {
            _listContainer.Clear();
            _emptyState.RemoveFromHierarchy();

            try
            {
                _cachedCheckpoints = CheckpointManager.ListCheckpoints();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[MCPServerWindow] Error listing checkpoints: {exception.Message}");
                _cachedCheckpoints = new List<CheckpointMetadata>();
            }

            if (_cachedCheckpoints.Count == 0)
            {
                _listScrollView.Add(_emptyState);
                return;
            }

            foreach (CheckpointMetadata checkpoint in _cachedCheckpoints)
            {
                _listContainer.Add(BuildCheckpointEntry(checkpoint));
            }
        }

        private VisualElement BuildCheckpointEntry(CheckpointMetadata checkpoint)
        {
            VisualElement entry = new VisualElement();
            entry.AddToClassList("checkpoint-entry");

            // Main row
            VisualElement mainRow = new VisualElement();
            mainRow.AddToClassList("row--spaced");

            // Left: name + scene
            VisualElement leftColumn = new VisualElement();
            leftColumn.style.flexGrow = 1;
            leftColumn.style.overflow = Overflow.Hidden;

            Label nameLabel = new Label(checkpoint.name);
            nameLabel.AddToClassList("checkpoint-name");
            leftColumn.Add(nameLabel);

            Label sceneLabel = new Label(checkpoint.sceneName);
            sceneLabel.AddToClassList("checkpoint-scene");
            leftColumn.Add(sceneLabel);

            Label idLabel = new Label(checkpoint.id);
            idLabel.AddToClassList("checkpoint-id");
            idLabel.AddToClassList("mono");
            leftColumn.Add(idLabel);

            mainRow.Add(leftColumn);

            // Center: timestamp
            VisualElement centerColumn = new VisualElement();
            centerColumn.style.flexShrink = 0;
            centerColumn.style.marginLeft = 8;
            centerColumn.style.marginRight = 8;

            Label timeLabel = new Label(checkpoint.timestamp.ToString("MMM dd, h:mm tt"));
            timeLabel.AddToClassList("checkpoint-time");
            centerColumn.Add(timeLabel);

            string relativeTime = GetRelativeTime(checkpoint.timestamp);
            Label relativeLabel = new Label(relativeTime);
            relativeLabel.AddToClassList("muted");
            centerColumn.Add(relativeLabel);

            mainRow.Add(centerColumn);

            // Right: action buttons
            VisualElement actionColumn = new VisualElement();
            actionColumn.AddToClassList("checkpoint-actions");

            string capturedId = checkpoint.id;

            Button diffButton = new Button(() => OnDiff(entry, capturedId)) { text = "Diff" };
            diffButton.AddToClassList("button--small");
            diffButton.AddToClassList("button--accent");
            actionColumn.Add(diffButton);

            Button restoreButton = new Button(() => OnRestoreClicked(entry, capturedId)) { text = "Restore" };
            restoreButton.AddToClassList("button--small");
            restoreButton.AddToClassList("button--warning");
            actionColumn.Add(restoreButton);

            mainRow.Add(actionColumn);
            entry.Add(mainRow);

            return entry;
        }

        #endregion

        #region Restore Flow

        private void OnRestoreClicked(VisualElement entry, string checkpointId)
        {
            // Remove any existing confirmation panel
            RemoveExpandablePanel(entry, "restore-confirm");

            VisualElement confirmPanel = new VisualElement();
            confirmPanel.name = "restore-confirm";
            confirmPanel.AddToClassList("checkpoint-confirm");

            Label confirmText = new Label("Restore to this checkpoint? An auto-checkpoint is saved first.");
            confirmText.AddToClassList("muted");
            confirmText.style.whiteSpace = WhiteSpace.Normal;
            confirmText.style.marginBottom = 4;
            confirmPanel.Add(confirmText);

            VisualElement buttonRow = new VisualElement();
            buttonRow.AddToClassList("row");

            Button confirmButton = new Button(() =>
            {
                try
                {
                    CheckpointMetadata restored = CheckpointManager.RestoreCheckpoint(checkpointId);
                    if (restored != null)
                    {
                        RebuildList();
                    }
                    else
                    {
                        Debug.LogWarning("[MCPServerWindow] Failed to restore checkpoint.");
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[MCPServerWindow] Error restoring checkpoint: {exception.Message}");
                }
            }) { text = "Confirm" };
            confirmButton.AddToClassList("button--small");
            confirmButton.AddToClassList("button--warning");
            buttonRow.Add(confirmButton);

            Button cancelButton = new Button(() => RemoveExpandablePanel(entry, "restore-confirm")) { text = "Cancel" };
            cancelButton.AddToClassList("button--small");
            cancelButton.style.marginLeft = 4;
            buttonRow.Add(cancelButton);

            confirmPanel.Add(buttonRow);
            entry.Add(confirmPanel);
        }

        #endregion

        #region Diff Flow

        private void OnDiff(VisualElement entry, string checkpointId)
        {
            // Toggle: remove if already showing
            VisualElement existing = entry.Q(name: "diff-panel");
            if (existing != null)
            {
                existing.RemoveFromHierarchy();
                return;
            }

            try
            {
                CheckpointDiff diff = CheckpointManager.GetDiff(checkpointId, "current");
                if (diff == null)
                {
                    Debug.LogWarning("[MCPServerWindow] Failed to compute diff.");
                    return;
                }

                VisualElement diffPanel = new VisualElement();
                diffPanel.name = "diff-panel";
                diffPanel.AddToClassList("checkpoint-diff");

                // Count changes
                Label countLabel = new Label($"Root objects: {diff.rootCountA} \u2192 {diff.rootCountB}");
                countLabel.AddToClassList("diff-changed");
                diffPanel.Add(countLabel);

                // Added objects
                if (diff.addedObjects != null && diff.addedObjects.Count > 0)
                {
                    foreach (string added in diff.addedObjects)
                    {
                        Label addedLabel = new Label($"+ {added}");
                        addedLabel.AddToClassList("diff-added");
                        diffPanel.Add(addedLabel);
                    }
                }

                // Removed objects
                if (diff.removedObjects != null && diff.removedObjects.Count > 0)
                {
                    foreach (string removed in diff.removedObjects)
                    {
                        Label removedLabel = new Label($"- {removed}");
                        removedLabel.AddToClassList("diff-removed");
                        diffPanel.Add(removedLabel);
                    }
                }

                // No changes
                if ((diff.addedObjects == null || diff.addedObjects.Count == 0) &&
                    (diff.removedObjects == null || diff.removedObjects.Count == 0))
                {
                    Label noChanges = new Label("No changes detected.");
                    noChanges.AddToClassList("muted");
                    diffPanel.Add(noChanges);
                }

                entry.Add(diffPanel);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[MCPServerWindow] Error computing diff: {exception.Message}");
            }
        }

        #endregion

        #region Helpers

        private static void RemoveExpandablePanel(VisualElement parent, string panelName)
        {
            VisualElement existing = parent.Q(name: panelName);
            existing?.RemoveFromHierarchy();
        }

        private static string GetRelativeTime(DateTime timestamp)
        {
            TimeSpan elapsed = DateTime.Now - timestamp;

            if (elapsed.TotalSeconds < 60) return "just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            return timestamp.ToString("MMM dd");
        }

        private static void SetPlaceholder(TextField textField, string placeholder)
        {
            Label placeholderLabel = new Label(placeholder);
            placeholderLabel.AddToClassList("muted");
            placeholderLabel.style.position = Position.Absolute;
            placeholderLabel.style.left = 4;
            placeholderLabel.style.top = 2;
            placeholderLabel.pickingMode = PickingMode.Ignore;

            textField.Add(placeholderLabel);

            textField.RegisterCallback<FocusInEvent>(evt => placeholderLabel.style.display = DisplayStyle.None);
            textField.RegisterCallback<FocusOutEvent>(evt =>
            {
                if (string.IsNullOrEmpty(textField.value))
                    placeholderLabel.style.display = DisplayStyle.Flex;
            });
        }

        #endregion
    }
}
