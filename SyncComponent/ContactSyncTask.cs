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

        // Сравнение полей контакта
        private bool AreContactsDifferent(Contact local, GoogleContact remote)
        {
            if (local.FirstName != remote.FirstName || local.LastName != remote.LastName) return true;

            var localPhones = local.Phones.Select(p => CleanPhone(p.Number)).OrderBy(p => p).ToList();
            var remotePhones = remote.Phones.Select(CleanPhone).OrderBy(p => p).ToList();

            if (localPhones.Count != remotePhones.Count) return true;
            for (int i = 0; i < localPhones.Count; i++)
                if (localPhones[i] != remotePhones[i]) return true;

            return false;
        }

        // Обновление контакта в Google (используем PATCH запрос)
        private async Task UpdateGoogleContactAsync(string accessToken, Contact localContact)
        {
            try
            {
                JsonObject root = new JsonObject();
                // Google требует при патче указывать, какие поля меняем
                root.SetNamedValue("updateMask", JsonValue.CreateStringValue("names,phoneNumbers"));

                JsonObject names = new JsonObject();
                names.SetNamedValue("givenName", JsonValue.CreateStringValue(localContact.FirstName ?? ""));
                names.SetNamedValue("familyName", JsonValue.CreateStringValue(localContact.LastName ?? ""));
                root.SetNamedValue("names", new JsonArray { names });

                JsonArray phones = new JsonArray();
                foreach (var p in localContact.Phones)
                    phones.Add(new JsonObject { { "value", JsonValue.CreateStringValue(p.Number) } });
                root.SetNamedValue("phoneNumbers", phones);

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    // Patch-запрос к Google API
                    var method = new HttpMethod("PATCH");
                    var request = new HttpRequestMessage(method, $"https://people.googleapis.com/v1/{localContact.RemoteId}:updateContact?updateMask=names,phoneNumbers")
                    {
                        Content = new StringContent(root.Stringify(), System.Text.Encoding.UTF8, "application/json")
                    };
                    await client.SendAsync(request);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Ошибка Patch: {ex.Message}"); }
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

            var syncedIds = await LoadSyncedIdsAsync();

            var existingContacts = new System.Collections.Generic.List<Contact>();
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

            // === ФАЗА 1: УДАЛЕНИЕ ИЗ GOOGLE ===
            foreach (var gc in googleContacts.ToList())
            {
                var localMatch = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                if (localMatch == null && syncedIds.Contains(gc.Id))
                {
                    System.Diagnostics.Debug.WriteLine($"[--] Удаление из Google (удален локально): {gc.FirstName} {gc.LastName}");
                    bool deleted = await DeleteGoogleContactAsync(accessToken, gc.Id);
                    if (deleted)
                    {
                        cloudDeletedCount++;
                        googleContacts.Remove(gc);
                    }
                }
            }

            // === ФАЗА 2: УДАЛЕНИЕ ИЗ ТЕЛЕФОНА ===
            var toDeleteLocally = existingContacts.Where(c => !string.IsNullOrEmpty(c.RemoteId) && !googleContacts.Any(gc => gc.Id == c.RemoteId)).ToList();
            foreach (var lc in toDeleteLocally)
            {
                System.Diagnostics.Debug.WriteLine($"[-] Удаление локально (пропал из Google): {lc.FirstName} {lc.LastName}");
                await myContactList.DeleteContactAsync(lc);
                existingContacts.Remove(lc);
                localDeletedCount++;
            }

            // === ФАЗА 3: UPLOAD И UPDATE В GOOGLE (Отправляем локальные изменения) ===
            foreach (var localContact in existingContacts)
            {
                if (!string.IsNullOrEmpty(localContact.RemoteId))
                {
                    // Обновляем существующий контакт в Google
                    var googleMatch = googleContacts.FirstOrDefault(gc => gc.Id == localContact.RemoteId);
                    if (googleMatch != null && AreContactsDifferent(localContact, googleMatch))
                    {
                        System.Diagnostics.Debug.WriteLine($"[^] Обновление контакта в Google: {localContact.FirstName} {localContact.LastName}");
                        await UpdateGoogleContactAsync(accessToken, localContact);
                        uploadedCount++;

                        // ВАЖНО: Обновляем данные в памяти, чтобы Фаза 4 не скачала старые данные обратно!
                        googleMatch.FirstName = localContact.FirstName;
                        googleMatch.LastName = localContact.LastName;
                        googleMatch.Phones.Clear();
                        foreach (var p in localContact.Phones) googleMatch.Phones.Add(p.Number);
                    }
                }
                else
                {
                    // Ищем дубликат в Google по имени (чтобы связать, а не создавать копию)
                    string localFullName = (localContact.FirstName + " " + localContact.LastName).Trim();
                    var possibleDuplicate = googleContacts.FirstOrDefault(gc =>
                        (gc.FirstName + " " + gc.LastName).Trim() == localFullName);

                    if (possibleDuplicate != null)
                    {
                        // Привязываем локальный контакт к Google ID
                        localContact.RemoteId = possibleDuplicate.Id;
                        await myContactList.SaveContactAsync(localContact);
                        linkedCount++;
                    }
                    else
                    {
                        // Создаем абсолютно новый контакт в Google
                        System.Diagnostics.Debug.WriteLine($"[^] Выгрузка в Google: {localContact.FirstName} {localContact.LastName}");
                        string newGoogleId = await CreateGoogleContactAsync(accessToken, localContact);

                        if (!string.IsNullOrEmpty(newGoogleId))
                        {
                            localContact.RemoteId = newGoogleId;
                            await myContactList.SaveContactAsync(localContact);
                            uploadedCount++;

                            // Добавляем в локальный список Google, чтобы не скачать на следующем шаге
                            googleContacts.Add(new GoogleContact { Id = newGoogleId, FirstName = localContact.FirstName, LastName = localContact.LastName });
                        }
                    }
                }
            }

            // === ФАЗА 4: DOWNLOAD И UPDATE ИЗ GOOGLE (Скачиваем новые изменения облака) ===
            foreach (var gc in googleContacts)
            {
                var match = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);

                if (match != null)
                {
                    // Если данные в Google отличаются от локальных (и мы их не обновляли в Фазе 3)
                    if (AreContactsDifferent(match, gc))
                    {
                        System.Diagnostics.Debug.WriteLine($"[!] Обновление локального контакта: {gc.FirstName} {gc.LastName}");

                        match.FirstName = gc.FirstName;
                        match.LastName = gc.LastName;
                        match.Phones.Clear();
                        foreach (var phone in gc.Phones)
                            match.Phones.Add(new ContactPhone { Number = phone, Kind = ContactPhoneKind.Mobile });

                        await myContactList.SaveContactAsync(match);
                    }
                }
                else
                {
                    // Создаем новый контакт из Google на телефоне
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
                    existingContacts.Add(contact);
                    downloadedCount++;
                    System.Diagnostics.Debug.WriteLine($"[+] Скачан контакт: {gc.FirstName} {gc.LastName}");
                }
            }

            // === ФИНАЛ: СОХРАНЯЕМ "ПАМЯТЬ" СИНХРОНИЗАЦИИ ===
            var newSyncedIds = existingContacts.Select(c => c.RemoteId).Where(id => !string.IsNullOrEmpty(id));
            await SaveSyncedIdsAsync(newSyncedIds);

            System.Diagnostics.Debug.WriteLine($"=== ЗАВЕРШЕНО. Связано: {linkedCount}, Скачано: {downloadedCount}, Выгружено/Обновлено: {uploadedCount}, Удал(Локал): {localDeletedCount}, Удал(Google): {cloudDeletedCount} ===");
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

                                if (person.ContainsKey("etag"))
                                    parsedContact.ETag = person.GetNamedString("etag");

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
        public string ETag { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public List<string> Phones { get; set; } = new List<string>();
    }
}