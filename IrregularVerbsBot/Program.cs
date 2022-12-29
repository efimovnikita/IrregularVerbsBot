using System.CommandLine;
using System.Text.Json.Serialization;
using CliWrap;
using CliWrap.Buffered;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Command = System.CommandLine.Command;

namespace IrregularVerbsBot
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // Define the 'run' command
            Option<string> keyOption = new("--key", "Telegram API key") { IsRequired = true };
            keyOption.AddAlias("-k");
            Option<FileInfo> memoOption = new("--memo", "Memorizer app path") { IsRequired = true };
            memoOption.AddAlias("-m");
            Command runCommand = new("run") { keyOption, memoOption };

            // Add a handler for the 'run' command
            RootCommand rootCommand = new("Irregular verbs bot");
            rootCommand.AddCommand(runCommand);

            runCommand.SetHandler(RunCommand, keyOption, memoOption);

            // Parse the command line arguments
            rootCommand.Invoke(args);
        }

        private static void RunCommand(string key, FileInfo memo)
        {
            Bot _ = new(key, memo);
        }
    }

    internal class Bot
    {
        private Dictionary<long, Queue<string?>> UserStates { get; set; } = new();
        private FileInfo MemoAppPath { get; }

        public Bot(string key, FileInfo memo)
        {
            MemoAppPath = memo;
            TelegramBotClient client = new(key);
            client.StartReceiving(UpdateHandler, PollingErrorHandler);

            Console.ReadLine();
        }

        private static Task PollingErrorHandler(
            ITelegramBotClient client,
            Exception exception,
            CancellationToken token
        )
        {
            Console.WriteLine(exception);
            return Task.CompletedTask;
        }

        // ReSharper disable once CognitiveComplexity
        private async Task UpdateHandler(
            ITelegramBotClient client,
            Update update,
            CancellationToken token
        )
        {
            Message? message = update.Message;
            if (message == null)
            {
                return;
            }

            if (message.Type != MessageType.Text) // working only with text
            {
                return;
            }

            long chatId = message.Chat.Id; // user ID
            string? text = message.Text;
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            #region Main logic

            if (text == "/start")
            {
                BufferedCommandResult result = await Cli.Wrap(MemoAppPath.FullName)
                    .WithArguments("verbs")
                    .WithWorkingDirectory(MemoAppPath.DirectoryName!)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (result.ExitCode != 0)
                {
                    return;
                }

                string output = result.StandardOutput;
                if (String.IsNullOrEmpty(output))
                {
                    return;
                }

                Queue<string?> queue =
                    new(
                        ShuffleList(
                            output
                                .Split(Environment.NewLine)
                                .Where(s => String.IsNullOrWhiteSpace(s) == false)
                                .ToArray()
                        )
                    );

                bool addResult = UserStates.TryAdd(chatId, queue);
                if (addResult == false)
                {
                    return;
                }

                await client.SendTextMessageAsync(
                    chatId,
                    $"Hello {message.Chat.Username}! Let's start!\nGive me a proper past and past participle form of the verbs (separate forms by whitespace).",
                    cancellationToken: token
                );

                if (queue.TryPeek(out string? queueElement) == false)
                {
                    return;
                }

                if (String.IsNullOrEmpty(queueElement))
                {
                    return;
                }

                await client.SendTextMessageAsync(
                    chatId,
                    $"\"{queueElement}\"",
                    cancellationToken: token
                );

                return;
            }

            if (text == "/stop")
            {
                if (UserStates.ContainsKey(chatId) == false)
                {
                    return;
                }

                await client.SendTextMessageAsync(chatId, "Done!", cancellationToken: token);
                UserStates.Remove(chatId);
                return;
            }

            if (UserStates.ContainsKey(chatId) == false)
            {
                return;
            }

            Queue<string?> verbs = UserStates[chatId];

            if (verbs.TryDequeue(out string? verb) == false)
            {
                await client.SendTextMessageAsync(chatId, "Done!", cancellationToken: token);
                UserStates.Remove(chatId);
                return;
            }

            BufferedCommandResult checkResult = await Cli.Wrap(MemoAppPath.FullName)
                .WithArguments($"check -v\"{verb}\" -f \"{text}\"")
                .WithWorkingDirectory(MemoAppPath.DirectoryName!)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (checkResult.ExitCode != 0)
            {
                await client.SendTextMessageAsync(
                    chatId,
                    "Check function returns error",
                    cancellationToken: token
                );
                await client.SendTextMessageAsync(chatId, "Done!", cancellationToken: token);
                UserStates.Remove(chatId);
                return;
            }

            string checkOutput = checkResult.StandardOutput;
            if (String.IsNullOrEmpty(checkOutput))
            {
                return;
            }

            try
            {
                ResultMsg? result = System.Text.Json.JsonSerializer.Deserialize<ResultMsg>(
                    checkOutput
                );
                if (result == null)
                {
                    await client.SendTextMessageAsync(
                        chatId,
                        $"Something went wrong during checking of the verb \"{verb}\"",
                        cancellationToken: token
                    );
                    return;
                }

                if (result.IsSuccess)
                {
                    await client.SendTextMessageAsync(
                        chatId,
                        "Correct! ",
                        cancellationToken: token
                    );
                }
                else
                {
                    await client.SendTextMessageAsync(
                        chatId,
                        $"Incorrect! The correct answer is: {result.Msg}",
                        cancellationToken: token
                    );
                }

                if (verbs.TryPeek(out string? queueElement) == false)
                {
                    await client.SendTextMessageAsync(chatId, "Done!", cancellationToken: token);
                    UserStates.Remove(chatId);
                    return;
                }

                if (String.IsNullOrEmpty(queueElement))
                {
                    return;
                }

                await client.SendTextMessageAsync(
                    chatId,
                    $"\"{queueElement}\"",
                    cancellationToken: token
                );
            }
            catch (System.Text.Json.JsonException)
            {
                await client.SendTextMessageAsync(
                    chatId,
                    $"Something went wrong during checking of the verb \"{verb}\"",
                    cancellationToken: token
                );
                await client.SendTextMessageAsync(chatId, "Done!", cancellationToken: token);
                UserStates.Remove(chatId);
            }

            #endregion
        }

        private static List<string> ShuffleList(IEnumerable<string> list)
        {
            // Create a new list containing the same elements as the original list
            List<string> shuffledList = new(list);

            // Initialize a new instance of the Random class
            Random rnd = new();

            // Shuffle the elements in the list
            for (int i = shuffledList.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (shuffledList[i], shuffledList[j]) = (shuffledList[j], shuffledList[i]);
            }

            // Return the shuffled list
            return shuffledList;
        }
    }

    [Serializable]
    internal class ResultMsg
    {
        [JsonPropertyName("is_success")]
        public bool IsSuccess { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }
    }
}
