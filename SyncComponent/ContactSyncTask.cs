using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Security.Credentials;

namespace SyncComponent
{
    public sealed class ContactSyncTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                var syncManager = new SyncManager();
                await syncManager.SyncNowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ФОН] Ошибка: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    public sealed class SyncManager
    {
        public IAsyncAction SyncNowAsync()
        {
            return ExecuteSyncAsync().AsAsyncAction();
        }

        private async Task ExecuteSyncAsync()
        {
            System.Diagnostics.Debug.WriteLine("=== НАЧАЛО ДВУСТОРОННЕЙ СИНХРОНИЗАЦИИ ===");

            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string clientId = localSettings.Values["GoogleClientId"] as string;
            string clientSecret = localSettings.Values["GoogleClientSecret"] as string;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return;

            var vault = new PasswordVault();
            var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();
            if (credentials == null) return;
            credentials.RetrievePassword();
            string refreshToken = credentials.Password;

            string accessToken = await GetAccessTokenAsync(refreshToken, clientId, clientSecret);
            if (string.IsNullOrEmpty(accessToken)) return;

            var googleContacts = await FetchGoogleContactsAsync(accessToken);
            if (googleContacts == null) return;

            var contactStore = await Windows.ApplicationModel.Contacts.ContactManager.RequestStoreAsync(
                Windows.ApplicationModel.Contacts.ContactStoreAccessType.AppContactsReadWrite);
            var contactLists = await contactStore.FindContactListsAsync();
            var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");

            if (myContactList == null)
            {
                myContactList = await contactStore.CreateContactListAsync("Контакты Google");
                myContactList.OtherAppReadAccess = ContactListOtherAppReadAccess.Full;
                await myContactList.SaveAsync();
            }

            // Загружаем память прошлых синхронизаций (список ID, которые уже были скачаны ранее)
            var syncedIds = await LoadSyncedIdsAsync();

            // Читаем все локальные контакты
            var existingContacts = new List<Contact>();
            var contactReader = myContactList.GetContactReader();
            var batch = await contactReader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                existingContacts.AddRange(batch.Contacts);
                batch = await contactReader.ReadBatchAsync();
            }

            int uploadedCount = 0;
            int downloadedCount = 0;
            int localDeletedCount = 0;
            int cloudDeletedCount = 0;
            int linkedCount = 0;

            // === ФАЗА 1: УДАЛЕНИЕ ИЗ GOOGLE (Локально удаленные контакты) ===
            foreach (var gc in googleContacts.ToList()) // Используем ToList, чтобы можно было удалять из оригинала
            {
                var localMatch = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);

                if (localMatch == null) // Контакта нет на телефоне
                {
                    if (syncedIds.Contains(gc.Id))
                    {
                        // Раньше он был синхронизирован! Значит пользователь удалил его с телефона.
                        System.Diagnostics.Debug.WriteLine($"[--] Удаление из Google (удален локально): {gc.FirstName} {gc.LastName}");
                        bool deleted = await DeleteGoogleContactAsync(accessToken, gc.Id);

                        if (deleted)
                        {
                            cloudDeletedCount++;
                            googleContacts.Remove(gc); // Убираем из списка, чтобы не скачать обратно в Фазе 3
                        }
                    }
                }
            }

            // === ФАЗА 2: УДАЛЕНИЕ ИЗ ТЕЛЕФОНА (Удаленные в Google) ===
            // Ищем те, у которых есть RemoteId, но их больше нет в свежем списке от Google
            var toDeleteLocally = existingContacts.Where(c => !string.IsNullOrEmpty(c.RemoteId) && !googleContacts.Any(gc => gc.Id == c.RemoteId)).ToList();
            foreach (var lc in toDeleteLocally)
            {
                System.Diagnostics.Debug.WriteLine($"[-] Удаление локально (пропал из Google): {lc.FirstName} {lc.LastName}");
                await myContactList.DeleteContactAsync(lc);
                existingContacts.Remove(lc); // Убираем из памяти
                localDeletedCount++;
            }

            // === ФАЗА 3: СКАЧИВАНИЕ И СВЯЗЫВАНИЕ ===
            foreach (var gc in googleContacts)
            {
                var match = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);

                // Если по ID не нашли, ищем по Имени + Совпадению телефонов
                if (match == null)
                {
                    string gName = (gc.FirstName + " " + gc.LastName).Trim();
                    var gPhones = gc.Phones.Select(CleanPhone).Where(p => p.Length > 0).ToList();

                    match = existingContacts.FirstOrDefault(c =>
                    {
                        if (!string.IsNullOrEmpty(c.RemoteId)) return false;

                        string lName = (c.FirstName + " " + c.LastName).Trim();
                        if (lName != gName) return false;

                        var lPhones = c.Phones.Select(p => CleanPhone(p.Number)).Where(p => p.Length > 0).ToList();

                        if (lPhones.Count == 0 && gPhones.Count == 0) return true;
                        return lPhones.Intersect(gPhones).Any();
                    });

                    if (match != null)
                    {
                        match.RemoteId = gc.Id;
                        await myContactList.SaveContactAsync(match);
                        linkedCount++;
                        System.Diagnostics.Debug.WriteLine($"[=] Связан локальный контакт: {gName}");
                    }
                }

                // Если контакта на телефоне вообще нет — создаем его
                if (match == null)
                {
                    var contact = new Contact
                    {
                        RemoteId = gc.Id,
                        FirstName = gc.FirstName,
                        LastName = gc.LastName,
                        Notes = "Синхронизировано из Google"
                    };

                    foreach (var phoneNum in gc.Phones)
                    {
                        if (!string.IsNullOrEmpty(phoneNum))
                            contact.Phones.Add(new ContactPhone { Number = phoneNum, Kind = ContactPhoneKind.Mobile });
                    }

                    await myContactList.SaveContactAsync(contact);
                    existingContacts.Add(contact); // Запоминаем для следующей фазы и для сохранения ID
                    downloadedCount++;
                    System.Diagnostics.Debug.WriteLine($"[+] Скачан контакт: {gc.FirstName} {gc.LastName}");
                }
            }

            // === ФАЗА 4: ВЫГРУЗКА В GOOGLE (Новые локальные контакты) ===
            var toUpload = existingContacts.Where(c => string.IsNullOrEmpty(c.RemoteId)).ToList();
            foreach (var localContact in toUpload)
            {
                System.Diagnostics.Debug.WriteLine($"[^] Выгрузка в Google: {localContact.FirstName} {localContact.LastName}");
                string newGoogleId = await CreateGoogleContactAsync(accessToken, localContact);

                if (!string.IsNullOrEmpty(newGoogleId))
                {
                    localContact.RemoteId = newGoogleId;
                    await myContactList.SaveContactAsync(localContact);
                    uploadedCount++;
                }
            }

            // === ФИНАЛ: СОХРАНЯЕМ "ПАМЯТЬ" СИНХРОНИЗАЦИИ ===
            var newSyncedIds = existingContacts.Select(c => c.RemoteId).Where(id => !string.IsNullOrEmpty(id));
            await SaveSyncedIdsAsync(newSyncedIds);

            System.Diagnostics.Debug.WriteLine($"=== ЗАВЕРШЕНО. Связано: {linkedCount}, Скачано: {downloadedCount}, Выгружено: {uploadedCount}, Удал(Локал): {localDeletedCount}, Удал(Google): {cloudDeletedCount} ===");
        }

        private string CleanPhone(string phone)
        {
            if (string.IsNullOrEmpty(phone)) return "";
            return phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace("+", "");
        }

        private async Task<string> GetAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        {
            using (var client = new HttpClient())
            {
                string requestBody = $"client_id={clientId}&client_secret={clientSecret}&refresh_token={refreshToken}&grant_type=refresh_token";
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

                var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    JsonObject json = JsonObject.Parse(jsonString);
                    return json.GetNamedString("access_token");
                }
            }
            return null;
        }

        private async Task<List<GoogleContact>> FetchGoogleContactsAsync(string accessToken)
        {
            var contactsList = new List<GoogleContact>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                string nextPageToken = "";
                bool hasMorePages = true;

                // Цикл для постраничной загрузки (пагинации)
                while (hasMorePages)
                {
                    // Указываем pageSize=1000 (максимум для Google API)
                    string requestUri = "https://people.googleapis.com/v1/people/me/connections?personFields=names,phoneNumbers&pageSize=1000";

                    // Если Google выдал токен следующей страницы в прошлом запросе, добавляем его в URL
                    if (!string.IsNullOrEmpty(nextPageToken))
                    {
                        requestUri += $"&pageToken={nextPageToken}";
                    }

                    var response = await client.GetAsync(requestUri);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonString = await response.Content.ReadAsStringAsync();
                        JsonObject json = JsonObject.Parse(jsonString);

                        if (json.ContainsKey("connections"))
                        {
                            JsonArray connections = json.GetNamedArray("connections");
                            foreach (var item in connections)
                            {
                                var person = item.GetObject();
                                var parsedContact = new GoogleContact();

                                if (person.ContainsKey("resourceName"))
                                    parsedContact.Id = person.GetNamedString("resourceName");

                                if (person.ContainsKey("names"))
                                {
                                    var names = person.GetNamedArray("names");
                                    if (names.Count > 0)
                                    {
                                        var primaryName = names[0].GetObject();
                                        parsedContact.FirstName = primaryName.ContainsKey("givenName") ? primaryName.GetNamedString("givenName") : "";
                                        parsedContact.LastName = primaryName.ContainsKey("familyName") ? primaryName.GetNamedString("familyName") : "";
                                    }
                                }

                                if (person.ContainsKey("phoneNumbers"))
                                {
                                    var phones = person.GetNamedArray("phoneNumbers");
                                    foreach (var pItem in phones)
                                        parsedContact.Phones.Add(pItem.GetObject().GetNamedString("value"));
                                }

                                if (!string.IsNullOrEmpty(parsedContact.FirstName) || parsedContact.Phones.Count > 0)
                                {
                                    contactsList.Add(parsedContact);
                                }
                            }
                        }

                        // Проверяем, есть ли еще страницы с контактами
                        if (json.ContainsKey("nextPageToken"))
                        {
                            nextPageToken = json.GetNamedString("nextPageToken");
                        }
                        else
                        {
                            // Если nextPageToken нет, значит мы дошли до конца списка
                            hasMorePages = false;
                        }
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        System.Diagnostics.Debug.WriteLine($"Ошибка скачивания из Google: {error}");
                        hasMorePages = false; // Прерываем цикл при ошибке
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"Всего скачано из Google (с учетом пагинации): {contactsList.Count}");
            return contactsList;
        }

        private async Task SaveSyncedIdsAsync(IEnumerable<string> ids)
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync("synced_ids.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, string.Join(",", ids));
        }

        private async Task<HashSet<string>> LoadSyncedIdsAsync()
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.GetFileAsync("synced_ids.txt");
                string content = await Windows.Storage.FileIO.ReadTextAsync(file);
                return new HashSet<string>(content.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch
            {
                return new HashSet<string>(); // Если файла нет, возвращаем пустой набор
            }
        }

        private async Task<string> CreateGoogleContactAsync(string accessToken, Contact localContact)
        {
            try
            {
                JsonObject root = new JsonObject();
                JsonArray names = new JsonArray();
                JsonObject nameObj = new JsonObject();
                nameObj.SetNamedValue("givenName", JsonValue.CreateStringValue(localContact.FirstName ?? ""));
                nameObj.SetNamedValue("familyName", JsonValue.CreateStringValue(localContact.LastName ?? ""));
                names.Add(nameObj);
                root.SetNamedValue("names", names);

                if (localContact.Phones.Count > 0)
                {
                    JsonArray phones = new JsonArray();
                    foreach (var p in localContact.Phones)
                    {
                        if (!string.IsNullOrEmpty(p.Number))
                        {
                            JsonObject phoneObj = new JsonObject();
                            phoneObj.SetNamedValue("value", JsonValue.CreateStringValue(p.Number));
                            phones.Add(phoneObj);
                        }
                    }
                    if (phones.Count > 0) root.SetNamedValue("phoneNumbers", phones);
                }

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var content = new StringContent(root.Stringify(), System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync("https://people.googleapis.com/v1/people:createContact", content);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonString = await response.Content.ReadAsStringAsync();
                        JsonObject responseJson = JsonObject.Parse(jsonString);
                        if (responseJson.ContainsKey("resourceName")) return responseJson.GetNamedString("resourceName");
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // НОВЫЙ МЕТОД: Удаление контакта из Google
        private async Task<bool> DeleteGoogleContactAsync(string accessToken, string resourceName)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    // Эндпоинт для удаления согласно Google People API
                    var response = await client.DeleteAsync($"https://people.googleapis.com/v1/{resourceName}:deleteContact");

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        System.Diagnostics.Debug.WriteLine($"Ошибка удаления из Google API: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка Delete: {ex.Message}");
            }
            return false;
        }
    }

    internal class GoogleContact
    {
        public string Id { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public List<string> Phones { get; set; } = new List<string>();
    }
}