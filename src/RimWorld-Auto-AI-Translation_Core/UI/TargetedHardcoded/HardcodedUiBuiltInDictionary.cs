using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    // Small, deliberately conservative dictionary for unambiguous UI controls.
    // It never decides whether text is player-visible; callers must first have
    // an effective Translate decision.
    internal static class HardcodedUiBuiltInDictionary
    {
        private sealed class Entry
        {
            internal string Role;
            internal string Source;
            internal string[] Values;
        }

        private static readonly Regex ProtectedTokenRegex = new Regex(
            @"(\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\]|\$[A-Za-z0-9_]+|%[A-Za-z]|<[^<>\r\n]+>)",
            RegexOptions.Compiled);

        private static readonly TargetLanguage[] LanguageOrder =
        {
            TargetLanguage.Traditional, TargetLanguage.Simplified, TargetLanguage.Japanese,
            TargetLanguage.Korean, TargetLanguage.Russian, TargetLanguage.Ukrainian,
            TargetLanguage.English, TargetLanguage.French, TargetLanguage.German,
            TargetLanguage.Spanish, TargetLanguage.Italian, TargetLanguage.Polish,
            TargetLanguage.Portuguese, TargetLanguage.Turkish
        };

        private static readonly Entry[] Entries =
        {
            E("button", "Close", "關閉", "关闭", "閉じる", "닫기", "Закрыть", "Закрити", "Close", "Fermer", "Schließen", "Cerrar", "Chiudi", "Zamknij", "Fechar", "Kapat"),
            E("button", "Cancel", "取消", "取消", "キャンセル", "취소", "Отмена", "Скасувати", "Cancel", "Annuler", "Abbrechen", "Cancelar", "Annulla", "Anuluj", "Cancelar", "İptal"),
            E("button", "Confirm", "確認", "确认", "確認", "확인", "Подтвердить", "Підтвердити", "Confirm", "Confirmer", "Bestätigen", "Confirmar", "Conferma", "Potwierdź", "Confirmar", "Onayla"),
            E("button", "Apply", "套用", "应用", "適用", "적용", "Применить", "Застосувати", "Apply", "Appliquer", "Anwenden", "Aplicar", "Applica", "Zastosuj", "Aplicar", "Uygula"),
            E("button", "Save", "儲存", "保存", "保存", "저장", "Сохранить", "Зберегти", "Save", "Enregistrer", "Speichern", "Guardar", "Salva", "Zapisz", "Salvar", "Kaydet"),
            E("button", "Reset", "重設", "重置", "リセット", "초기화", "Сбросить", "Скинути", "Reset", "Réinitialiser", "Zurücksetzen", "Restablecer", "Reimposta", "Resetuj", "Redefinir", "Sıfırla"),
            E("label", "Search", "搜尋", "搜索", "検索", "검색", "Поиск", "Пошук", "Search", "Rechercher", "Suchen", "Buscar", "Cerca", "Szukaj", "Pesquisar", "Ara"),
            E("label", "Settings", "設定", "设置", "設定", "설정", "Настройки", "Налаштування", "Settings", "Paramètres", "Einstellungen", "Ajustes", "Impostazioni", "Ustawienia", "Configurações", "Ayarlar"),
            E("status", "Enabled", "已啟用", "已启用", "有効", "활성화됨", "Включено", "Увімкнено", "Enabled", "Activé", "Aktiviert", "Activado", "Attivato", "Włączone", "Ativado", "Etkin"),
            E("status", "Disabled", "已停用", "已禁用", "無効", "비활성화됨", "Отключено", "Вимкнено", "Disabled", "Désactivé", "Deaktiviert", "Desactivado", "Disattivato", "Wyłączone", "Desativado", "Devre dışı"),
            E("key", "Name", "名稱", "名称", "名前", "이름", "Название", "Назва", "Name", "Nom", "Name", "Nombre", "Nome", "Nazwa", "Nome", "Ad"),
            E("key", "Description", "說明", "说明", "説明", "설명", "Описание", "Опис", "Description", "Description", "Beschreibung", "Descripción", "Descrizione", "Opis", "Descrição", "Açıklama")
        };

        internal static bool TryTranslate(
            string source,
            string semanticRole,
            TargetLanguage targetLanguage,
            out string translated)
        {
            translated = string.Empty;
            string normalizedSource = (source ?? string.Empty).Trim();
            string normalizedRole = NormalizeRole(semanticRole);
            if (normalizedSource.Length == 0 || normalizedRole.Length == 0) return false;

            Entry entry = Entries.FirstOrDefault(dictionaryEntry =>
                string.Equals(dictionaryEntry.Role, normalizedRole, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dictionaryEntry.Source, normalizedSource, StringComparison.OrdinalIgnoreCase));
            int languageIndex = Array.IndexOf(LanguageOrder, targetLanguage);
            if (entry == null || languageIndex < 0 || languageIndex >= entry.Values.Length) return false;

            string result = (entry.Values[languageIndex] ?? string.Empty).Trim();
            if (!IsValidTranslation(normalizedSource, result, targetLanguage)) return false;
            translated = result;
            return true;
        }

        internal static bool IsValidTranslation(
            string source,
            string translated,
            TargetLanguage targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated)) return false;
            if (string.Equals(source.Trim(), translated.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (!ProtectedTokensMatch(source, translated)) return false;
            if (LanguageDetector.LooksLikePlaceholderTranslation(translated, targetLanguage)) return false;
            return TranslationResultLanguagePolicy.ShouldAccept(translated, source, targetLanguage);
        }

        private static string NormalizeRole(string role)
        {
            string normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "key-name" || normalized == "key_name" || normalized == "keyname") return "key";
            return normalized;
        }

        private static bool ProtectedTokensMatch(string source, string translated)
        {
            string[] sourceTokens = ProtectedTokenRegex.Matches(source ?? string.Empty)
                .Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] translatedTokens = ProtectedTokenRegex.Matches(translated ?? string.Empty)
                .Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return sourceTokens.SequenceEqual(translatedTokens, StringComparer.Ordinal);
        }

        private static Entry E(string role, string source, params string[] values)
        {
            return new Entry { Role = role, Source = source, Values = values };
        }
    }
}
