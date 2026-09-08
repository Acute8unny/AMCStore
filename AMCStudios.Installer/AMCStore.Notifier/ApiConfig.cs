namespace AMCStore.Notifier
{
    public static class ApiConfig
    {
        public const string ApiBaseUrl = "https://acutebunny.pythonanywhere.com";

        public static Uri Url(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new Uri(ApiBaseUrl);
            }
            return new Uri($"{ApiBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        }
    }
}