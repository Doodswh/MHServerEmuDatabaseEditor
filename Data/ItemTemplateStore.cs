using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    // Loads/saves the list of ItemTemplates to a JSON file sitting next to the built exe, so
    // it travels with the tool when you hand it to someone else and survives between runs
    // without needing a database connection.
    public class ItemTemplateStore
    {
        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "item_templates.json");

        private List<ItemTemplate> _templates = new();

        public IReadOnlyList<ItemTemplate> Templates => _templates;

        public string LoadError { get; private set; }

        public void Load()
        {
            LoadError = null;
            if (File.Exists(FilePath) == false)
            {
                _templates = new List<ItemTemplate>();
                return;
            }

            try
            {
                string json = File.ReadAllText(FilePath);
                _templates = JsonSerializer.Deserialize<List<ItemTemplate>>(json) ?? new List<ItemTemplate>();
            }
            catch (Exception ex)
            {
                // Keep the bad file so the next save doesn't wipe the templates in it.
                _templates = new List<ItemTemplate>();
                LoadError = $"Couldn't read {Path.GetFileName(FilePath)} ({ex.Message}). Starting with no templates";
                try { File.Copy(FilePath, FilePath + ".bad", true); LoadError += $"; the file was copied to {Path.GetFileName(FilePath)}.bad"; }
                catch (Exception) { }
                LoadError += ".";
            }
        }

        public void Add(ItemTemplate template)
        {
            _templates.Add(template);
            Save();
        }

        public void Remove(ItemTemplate template)
        {
            _templates.Remove(template);
            Save();
        }

        private void Save()
        {
            string json = JsonSerializer.Serialize(_templates, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
