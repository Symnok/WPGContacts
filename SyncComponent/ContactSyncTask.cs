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

    internal class SyncState
    {
        public System.Collections.Generic.Dictionary<string, string> Etags = new System.Collections.Generic.Dictionary<string, string>();
        public System.Collections.Generic.Dictionary<string, string> Hashes = new System.Collections.Generic.Dictionary<string, string>();
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
        private async Task<bool> UpdateGoogleContactAsync(string accessToken, Contact localContact, string etag)
        {
            try
            {
                // 1. Формируем "чистый" объект контакта
                JsonObject person = new JsonObject();

                JsonArray names = new JsonArray();
                JsonObject nameObj = new JsonObject();
                nameObj.SetNamedValue("givenName", JsonValue.CreateStringValue(localContact.FirstName ?? ""));
                nameObj.SetNamedValue("familyName", JsonValue.CreateStringValue(localContact.LastName ?? ""));
                names.Add(nameObj);
                person.SetNamedValue("names", names);

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
                person.SetNamedValue("phoneNumbers", phones);
                person.SetNamedValue("etag", JsonValue.CreateStringValue(etag));

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    // ВАЖНО: 
                    // 1. Используем :updateContact
                    // 2. Маска updateMask=names,phoneNumbers ОБЯЗАТЕЛЬНА в URL для этого метода
                    // 3. ID должен начинаться с people/, если RemoteId его не содержит, добавьте вручную
                    string resourceId = localContact.RemoteId;
                    if (!resourceId.StartsWith("people/")) resourceId = "people/" + resourceId;

                    string url = $"https://people.googleapis.com/v1/{resourceId}:updateContact?updatePersonFields=names,phoneNumbers";

                    // Используем PATCH
                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = new StringContent(person.Stringify(), System.Text.Encoding.UTF8, "application/json")
                    };

                    var response = await client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"[OK] Google API обновил контакт {localContact.FirstName}");
                        return true;
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        System.Diagnostics.Debug.WriteLine($"[ERROR] Google API отклонён PATCH: {error}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CRASH] UpdateGoogleContactAsync: {ex.Message}");
                return false;
            }
        }
        private async Task SaveSyncStateAsync(Dictionary<string, string> etags, Dictionary<string, string> hashes)
        {
            JsonObject root = new JsonObject();
            JsonObject etagsJson = new JsonObject();
            JsonObject hashesJson = new JsonObject();

            foreach (var kvp in etags) etagsJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            foreach (var kvp in hashes) hashesJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));

            root.SetNamedValue("etags", etagsJson);
            root.SetNamedValue("hashes", hashesJson);

            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync("sync_state.json", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, root.Stringify());
        }
        private async Task<SyncState> LoadSyncStateAsync()
        {
            var state = new SyncState();
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;

            // TryGetItemAsync возвращает null вместо ошибки, если файла нет
            var item = await folder.TryGetItemAsync("sync_state.json");

            if (item != null && item is Windows.Storage.IStorageFile)
            {
                try
                {
                    var file = (Windows.Storage.StorageFile)item;
                    string jsonString = await Windows.Storage.FileIO.ReadTextAsync(file);

                    if (!string.IsNullOrEmpty(jsonString))
                    {
                        JsonObject root = JsonObject.Parse(jsonString);

                        if (root.ContainsKey("etags"))
                        {
                            var etagsObj = root.GetNamedObject("etags");
                            foreach (var key in etagsObj.Keys)
                                state.Etags[key] = etagsObj.GetNamedString(key);
                        }

                        if (root.ContainsKey("hashes"))
                        {
                            var hashesObj = root.GetNamedObject("hashes");
                            foreach (var key in hashesObj.Keys)
                                state.Hashes[key] = hashesObj.GetNamedString(key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Ошибка парсинга sync_state.json: " + ex.Message);
                }
            }

            return state;
        }



        private string CalculateHash(string f, string l, List<string> phones)
        {
            // Обязательно Trim() и к нижнему регистру для стабильности сравнения
            string first = (f ?? "").Trim().ToLower();
            string last = (l ?? "").Trim().ToLower();
            var pList = phones.Select(p => CleanPhone(p)).OrderBy(p => p).ToList();

            string raw = string.Format("{0}|{1}|{2}", first, last, string.Join(",", pList));

            var buffer = Windows.Security.Cryptography.CryptographicBuffer.ConvertStringToBinary(raw, Windows.Security.Cryptography.BinaryStringEncoding.Utf8);
            var hashAlg = Windows.Security.Cryptography.Core.HashAlgorithmProvider.OpenAlgorithm(Windows.Security.Cryptography.Core.HashAlgorithmNames.Sha1);
            var hashed = hashAlg.HashData(buffer);
            return Windows.Security.Cryptography.CryptographicBuffer.EncodeToHexString(hashed);
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

            var state = await LoadSyncStateAsync();
            var newEtags = new Dictionary<string, string>();
            var newHashes = new Dictionary<string, string>();

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

           // var etagMap = await LoadEtagsAsync();

            // === ФАЗА 1: УДАЛЕНИЕ ИЗ GOOGLE ===
            foreach (var gc in googleContacts.ToList())
            {
                var localMatch = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);

                if (localMatch == null) // Контакта нет на телефоне
                {
                    // Проверяем "память": был ли этот ID у нас в прошлой синхронизации?
                    if (state.Etags.ContainsKey(gc.Id))
                    {
                        // Был! Значит, пользователь удалил его из телефонной книги.
                        System.Diagnostics.Debug.WriteLine($"[--] Удаление из Google (удален локально): {gc.FirstName}");
                        bool deleted = await DeleteGoogleContactAsync(accessToken, gc.Id);

                        if (deleted)
                        {
                            googleContacts.Remove(gc);
                            cloudDeletedCount++;
                            // Убираем его из памяти ETag и Хешей, чтобы не пытаться обработать снова
                            state.Etags.Remove(gc.Id);
                            state.Hashes.Remove(gc.Id);
                        }
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

            // 4. ГЛАВНЫЙ ЦИКЛ СИНХРОНИЗАЦИИ И РАЗРЕШЕНИЯ КОНФЛИКТОВ
            foreach (var gc in googleContacts)
            {
                var lc = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                string googleHash = CalculateHash(gc.FirstName, gc.LastName, gc.Phones.Select(CleanPhone).ToList());
                if (lc != null) // Контакт есть и там, и там. Проверяем, КТО изменился.
                {
                    string localHash = CalculateHash(lc.FirstName, lc.LastName, lc.Phones.Select(p => CleanPhone(p.Number)).ToList());

                    string lastHash = state.Hashes.ContainsKey(gc.Id) ? state.Hashes[gc.Id] : "";
                    string lastEtag = state.Etags.ContainsKey(gc.Id) ? state.Etags[gc.Id] : "";

                    // ПРОВЕРЯЕМ ИЗМЕНЕНИЯ
                    // 1. Изменилось ли что-то в облаке? (сравниваем ETag или хеш данных)
                    bool cloudChanged = !string.IsNullOrEmpty(lastEtag) && gc.ETag != lastEtag;

                    // Проверяем: поменялся ли Телефон?
                    // Если lastHash пустой (первый запуск), считаем, что телефон НЕ менялся (просто связываем)
                    bool localChanged = !string.IsNullOrEmpty(lastHash) && localHash != lastHash;

                    if (cloudChanged)
                    {
                        System.Diagnostics.Debug.WriteLine($"[!] Смена в ОБЛАКЕ для {gc.FirstName}");
                        // Обновляем телефон (как у вас было)
                        lc.FirstName = gc.FirstName;
                        lc.LastName = gc.LastName;
                        lc.Phones.Clear();
                        foreach (var p in gc.Phones) lc.Phones.Add(new ContactPhone { Number = p });
                        await myContactList.SaveContactAsync(lc);

                        // Запоминаем состояние Google как эталон
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                    }
                    else if (localChanged)
                    {
                        // ПРИОРИТЕТ ТЕЛЕФОНА: Обновляем Google
                        System.Diagnostics.Debug.WriteLine($"[^] Локальное изменение в телефоне: {lc.FirstName}. Обновляем Google.");
                        bool ok = await UpdateGoogleContactAsync(accessToken, lc, gc.ETag);

                        // В память записываем текущий хеш телефона
                        if (ok)
                        {
                            newHashes[gc.Id] = localHash;
                            // ETag обновится при следующем скачивании, пока оставим старый
                            newEtags[gc.Id] = gc.ETag;
                        }
                        else
                        {
                            // Если не удалось обновить Google, оставляем СТАРЫЙ хеш в памяти,
                            // чтобы при следующем запуске программа снова увидела разницу и попробовала еще раз
                            newHashes[gc.Id] = lastHash;
                            newEtags[gc.Id] = lastEtag;
                        }
                    }
                    else {
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                    }
                    
                }
                else
                //if (!syncedIds.Contains(gc.Id))
                {
                    // НОВЫЙ КОНТАКТ ИЗ ОБЛАКА: Скачиваем
                    var contact = new Contact { RemoteId = gc.Id, FirstName = gc.FirstName, LastName = gc.LastName };
                    foreach (var p in gc.Phones) contact.Phones.Add(new ContactPhone { Number = p });
                    await myContactList.SaveContactAsync(contact);

                    newEtags[gc.Id] = gc.ETag;
                    newHashes[gc.Id] = googleHash;
                    System.Diagnostics.Debug.WriteLine($"[+] Новый контакт из Google: {gc.FirstName}");
                }
            }

            // 5. ВЫГРУЗКА НОВЫХ ЛОКАЛЬНЫХ (у которых еще нет RemoteId)
            foreach (var lc in existingContacts.Where(c => string.IsNullOrEmpty(c.RemoteId)))
            {
                System.Diagnostics.Debug.WriteLine($"[^] Upload New: '{lc.FirstName}' -> Google");
                string newId = await CreateGoogleContactAsync(accessToken, lc);
                if (!string.IsNullOrEmpty(newId))
                {
                    lc.RemoteId = newId;
                    await myContactList.SaveContactAsync(lc);

                    // Сохраняем хеш для следующей проверки
                    string hash = CalculateHash(lc.FirstName, lc.LastName, lc.Phones.Select(p => CleanPhone(p.Number)).ToList());
                    newHashes[newId] = hash;
                    // ETag подтянется при следующей синхронизации
                }
            }

            // 6. ФИНАЛ: СОХРАНЯЕМ ТЕКУЩЕЕ СОСТОЯНИЕ ОБЛАКА КАК "ПАМЯТЬ"
            //await SaveSyncedIdsAsync(existingContacts.Select(c => c.RemoteId).Where(id => !string.IsNullOrEmpty(id)));
            await SaveSyncStateAsync(newEtags, newHashes);
            // Мы сохраняем именно те ETag-и, которые прислал Google в ЭТОЙ итерации
            //await SaveEtagsAsync(currentCloudEtags);
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

        private string GetEtagForContact(string remoteId)
{
    // Здесь нужно считать из файла/настроек сохраненный ETag для данного ID
    // Например: return localSettings.Values["ETag_" + remoteId] as string;
    return ""; // Пока верните пустоту, если не сохраняли, но Google может продолжать требовать
}

       /* private async Task<System.Collections.Generic.Dictionary<string, string>> LoadEtagsAsync()
        {
            var etags = new System.Collections.Generic.Dictionary<string, string>();
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.GetFileAsync("etags.json");
                string jsonString = await Windows.Storage.FileIO.ReadTextAsync(file);

                JsonObject root = JsonObject.Parse(jsonString);
                foreach (var key in root.Keys)
                {
                    etags[key] = root.GetNamedString(key);
                }
            }
            catch { * Файла еще нет  }
            return etags;
        }*/

        private async Task SaveEtagsAsync(System.Collections.Generic.Dictionary<string, string> etags)
        {
            JsonObject root = new JsonObject();
            foreach (var kvp in etags)
            {
                root.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            }

            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync("etags.json", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, root.Stringify());
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