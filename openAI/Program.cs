using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using DotNetEnv;
using Microsoft.Extensions.Logging;



internal class Program
{
	private static readonly string OpenAIUrl = "https://api.openai.com/v1/chat/completions";

	private static string modelAPI = "gpt-3.5-turbo";

	public static readonly ILogger logger = LoggerFactory.Create(builder=>builder.AddConsole()).CreateLogger<Program>();

    private static Dictionary<string, List<dynamic>> userHistories = new Dictionary<string, List<dynamic>>();
	private static async Task Main(string[] args)
	{
		Console.CursorVisible = false;
        DotNetEnv.Env.TraversePath().Load();

        using var cts = new CancellationTokenSource();
		var bot = new TelegramBotClient(DotNetEnv.Env.GetString("BotToken"), cancellationToken: cts.Token);

        var me = await bot.GetMeAsync();
		bot.OnMessage += OnMessage;

		logger.LogInformation($"@{me.Username} начал работу в {DateTime.Now}\n");

		await Task.Delay(-1);
		cts.Cancel();

        async Task OnMessage(Message msg, UpdateType type)
		{
			if (msg.Text == null) return;

			logger.LogInformation($"({msg.Chat.Id}) @{msg.Chat.Username}\nСообщение '{msg.Text}'\n{DateTime.Now}\n");

			switch (msg.Text)
			{
				case "/start":
					await bot.SendTextMessageAsync(
						chatId: msg.Chat.Id, 
						text: "👋 Привет! Я ваш персональный помощник, готовый помочь вам с любыми вопросами. 🚀\r\nЯ могу:\r\n\r\n✨ Ответить на интересующие вас вопросы.\r\n📚 Помочь с учебой, работой или творчеством.\r\n\U0001f9e0 Найти новые идеи и решения.\r\nПросто напишите, что вам нужно, и я постараюсь помочь! 💬"
					);
					return;
				case "/clear":
                    if (userHistories.ContainsKey(msg.Chat.Id.ToString()))
                    {
                        userHistories.Remove(msg.Chat.Id.ToString());
                        await bot.SendTextMessageAsync(
							chatId: msg.Chat.Id, 
							text: "\U0001f9f9 История запросов очищена!\r\n\r\nТеперь мы начинаем с чистого листа. ✨ Вы можете задавать новые вопросы или продолжить общение, как будто мы только что познакомились.\r\n\r\n💡 Не стесняйтесь делиться своими идеями или задачами – я всегда готов помочь! 😊\r\n\r\nЧто хотите обсудить? 📝"
						);
                    }
					else
					{
                        await bot.SendTextMessageAsync(
							chatId: msg.Chat.Id, 
							text: "\U0001f9f9 История запросов очищена!\r\n\r\nКажется, у нас еще не было истории, но это не проблема – всегда есть время начать что-то новое. 😊\r\n\r\nНапишите, что вас интересует, и я с радостью помогу! 💬");
                    }
					return;
            }

			try
			{
                if (!userHistories.ContainsKey(msg.Chat.Id.ToString())) // проверка на наличие истории чата
                {
                    userHistories[msg.Chat.Id.ToString()] = new List<dynamic>
					{
						new { 
							role = "system", 
							content = "Ты — помощник." 
						}
					};
                }

                // Добавляем сообщение пользователя в историю
                userHistories[msg.Chat.Id.ToString()].Add(new { role = "user", content = msg.Text });

                TrimHistory(msg.Chat.Id.ToString()); // проверка истории на длину



                // сообщение о том, что запрос отправлен и обрабатывается
                var initialMessage = await bot.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: $"⏳ Обрабатываю ваш запрос...",
                    parseMode: ParseMode.Html
                );

                await bot.SendChatActionAsync(chatId: msg.Chat.Id, ChatAction.Typing);// динамический текст сверху, который означает, что бот пишет


                string response = await SendMessageToOpenAI(msg.Chat.Id.ToString()); // запрос к API
                string formattedResponse = ConvertToHtml(response); // форматирование текста от API

                // Добавляем ответ API в историю
                userHistories[msg.Chat.Id.ToString()].Add(
					new { 
						role = "assistant", 
						content = response 
					});

                // Обновляем сообщение с ответом от API
                await bot.EditMessageTextAsync(
                    chatId: msg.Chat.Id,
                    messageId: initialMessage.MessageId,
                    text: formattedResponse,
                    parseMode: ParseMode.Html
                );
                

            }
			catch (Exception ex)
			{

				logger.LogError($"Ошибка при запросе к OpenAI API: {ex.Message}");
				await bot.SendTextMessageAsync(
					chatId: msg.Chat.Id, 
					text: "⚠️ Упс! Что-то пошло не так.\r\n\r\nПроизошла ошибка при обработке вашего запроса. Возможно, это временная проблема.\r\n\r\nЕсли ошибка повторяется, обратитесь к разработчику. 🙏\r\n@tumples\r\nИзвините за неудобства! 😊"
				);
			}
		}

		async Task<string> SendMessageToOpenAI(string userId)
		{
			var proxy = new WebProxy // подключение к прокси
			{
				Address = new Uri(DotNetEnv.Env.GetString("Address")),
				BypassProxyOnLocal = false,
				UseDefaultCredentials = false,
				Credentials = new NetworkCredential(DotNetEnv.Env.GetString("Login"), DotNetEnv.Env.GetString("Password"))
			};

			var httpClientHandler = new HttpClientHandler
			{
				Proxy = proxy,
				ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
			};

			using (HttpClient client = new HttpClient(httpClientHandler))
			{
				client.DefaultRequestHeaders.Add("Authorization", $"Bearer {DotNetEnv.Env.GetString("OpenAIToken")}");

				var requestData = new
				{
					model = modelAPI,
					messages = userHistories[userId].ToArray() // Отправляем всю историю чата
				};

				string jsonRequest = JsonConvert.SerializeObject(requestData);
				StringContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

				HttpResponseMessage response = await client.PostAsync(OpenAIUrl, content);

				if (!response.IsSuccessStatusCode)
				{
					string errorMessage = await response.Content.ReadAsStringAsync();
					throw new Exception($"Ошибка API: {response.StatusCode} - {errorMessage}");
				}

				string responseContent = await response.Content.ReadAsStringAsync();
				var jsonResponse = JsonConvert.DeserializeObject<dynamic>(responseContent);
				return jsonResponse.choices[0].message.content.ToString();
			}
		}


        static string ConvertToHtml(string message)
        {
            // Создаем словарь для хранения блоков кода
            var codeBlocks = new Dictionary<string, string>();

            // Замена специальных HTML-символов заранее, чтобы избежать их замены в содержимом блоков кода
            message = message.Replace("&", "&amp;")
                             .Replace("<", "&lt;")
                             .Replace(">", "&gt;")
                             .Replace("'", "&apos;");

            // Обработка блоков кода в формате ```код```
            message = Regex.Replace(message, @"(?<!\\)```(\S+)?\n(.*?)\n```", match =>
            {
                // Генерация уникального ключа для каждого блока кода
                string uniqueKey = Guid.NewGuid().ToString();
                codeBlocks[uniqueKey] = match.Value;
                return uniqueKey; // Замена блока кода на ключ
            }, RegexOptions.Singleline);

            // Преобразование блоков кода в HTML с поддержкой языка программирования
            foreach (var key in codeBlocks.Keys.ToList())
            {
                string block = codeBlocks[key];
                var match = Regex.Match(block, @"```(\S+)?\n(.*?)\n```", RegexOptions.Singleline);
                if (match.Success)
                {
                    // Получение языка программирования (если указан) и содержимого кода
                    string language = match.Groups[1].Success ? $" class=\"language-{match.Groups[1].Value}\"" : "";
                    string code = match.Groups[2].Value.Trim();
                    // Форматирование блока кода в HTML
                    codeBlocks[key] = $"<pre><code{language}>{code}</code></pre>";
                }
            }

            // Обработка встроенного кода в формате `код`
            message = Regex.Replace(message, @"(?<!\\)`(.+?)`", match =>
                $"<code>{match.Groups[1].Value}</code>");

            // Обработка заголовков, начинающихся с #
            message = Regex.Replace(message, @"^(#+)\s*(.+)$", match => {
                int level = match.Groups[1].Value.Length;
                return $"<blockquote>{match.Groups[2].Value}</blockquote>";
            }, RegexOptions.Multiline);

            // Обработка цитат, начинающихся с >
            message = Regex.Replace(message, @"^&gt;\s*(.+)$", "<blockquote>$1</blockquote>", RegexOptions.Multiline);

            // Обработка жирного текста (***текст*** или **текст**)
            message = Regex.Replace(message, @"\*\*\*(.*?)\*\*\*", "<strong>$1</strong>");
            message = Regex.Replace(message, @"\*\*(.*?)\*\*", "<strong>$1</strong>");

            // Обработка курсива (*текст*)
            message = Regex.Replace(message, @"\*(.*?)\*", "<em>$1</em>");

            // Обработка зачеркнутого текста (~~текст~~)
            message = Regex.Replace(message, @"~~(.*?)~~", "<del>$1</del>");

            // Обработка ссылок [текст](ссылка)
            message = Regex.Replace(message, @"\[(.*?)\]\((.*?)\)", "<a href='$2'>$1</a>");

            // Удаление горизонтальных линий (---)
            message = Regex.Replace(message, @"---", "");

            // Вставка обратно обработанных блоков кода
            foreach (var key in codeBlocks.Keys)
            {
                message = message.Replace(key, codeBlocks[key]);
            }

            // Возврат обработанного сообщения
            return message;
        }

        static void TrimHistory(string userId)
		{
			// Удаляем 2 самых старых записи, если количество записей превышает 8
			if (userHistories[userId].Count > 8)
			{
				userHistories[userId].RemoveRange(0, 2);
			}
		}
	}
}

//mistral AI
/*
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;

internal class Program
{
	private static readonly string BotToken = "";
	private static readonly string ApiToken = "";
	private static readonly string ApiUrl = "https://api.mistral.ai/v1/chat/completions";

	private static async Task Main(string[] args)
	{
		using var cts = new CancellationTokenSource();
		var bot = new TelegramBotClient(BotToken, cancellationToken: cts.Token);
		Console.Clear();

		var me = await bot.GetMeAsync();
		bot.OnMessage += OnMessage;

		Console.ForegroundColor = ConsoleColor.DarkGreen;
		logger.LogInformation($"@{me.Username} начал работу в {DateTime.Now}\n");
		Console.ResetColor();
		Console.ReadLine();
		cts.Cancel();

		async Task OnMessage(Message msg, UpdateType type)
		{
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			logger.LogInformation($"({msg.Chat.Id}) @{msg.Chat.Username}\nСообщение '{msg.Text}'\n{DateTime.Now}\n");
			Console.ResetColor();

			// Отправка сообщения в Mistral AI и получение ответа
			string response = await SendMessageToMistralAI(msg.Text);
			await bot.SendTextMessageAsync(msg.Chat.Id, response);
		}

		async Task<string> SendMessageToMistralAI(string message)
		{
			using (HttpClient client = new HttpClient())
			{
				client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiToken}");

				var requestData = new
				{
					model = "mistral-small-latest",
					messages = new[]
					{
						new { role = "user", content = message }
					}
				};

				string jsonRequest = JsonConvert.SerializeObject(requestData);
				StringContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

				HttpResponseMessage response = await client.PostAsync(ApiUrl, content);

				if (!response.IsSuccessStatusCode)
				{
					string errorMessage = await response.Content.ReadAsStringAsync();
					throw new Exception($"Ошибка API: {response.StatusCode} - {errorMessage}");
				}

				string responseContent = await response.Content.ReadAsStringAsync();
				var jsonResponse = JsonConvert.DeserializeObject<dynamic>(responseContent);
				return jsonResponse.choices[0].message.content.ToString();
			}
		}
	}
}
*/