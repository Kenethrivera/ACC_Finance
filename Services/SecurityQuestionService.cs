using System.Security.Cryptography;

namespace acc_finance.Services
{
    public class SecurityQuestionService
    {
        private readonly List<SecurityQuestion> _questions = new()
        {
            new SecurityQuestion { Id = 1, Question = "Who is the Senior Pastor of ACC?", Answer = "FERNANDO SYDNEY T. HERRERA", ShowPuzzle = true },
            new SecurityQuestion { Id = 2, Question = "Who is the Treasurer of ACC?", Answer = "Cora Malaga", ShowPuzzle = true },
            new SecurityQuestion { Id = 3, Question = "What are the daughter churches of ACC called?", Answer = "Pinagpala Family Center", ShowPuzzle = true },
            new SecurityQuestion { Id = 4, Question = "Who is the Assistant Treasurer of ACC?", Answer = "Hanna Guigayoma", ShowPuzzle = true },
            new SecurityQuestion { Id = 5, Question = "Who is the Internal Auditor of ACC?", Answer = "Imelda Marcilla", ShowPuzzle = true },
            new SecurityQuestion { Id = 6, Question = "Who is the developer of this system?", Answer = "Keneth Rivera", ShowPuzzle = true }
        };

        public SecurityQuestion GetRandomQuestion(int? excludeId = null)
        {
            var available = _questions.Where(q => q.Id != excludeId).ToList();
            int index = RandomNumberGenerator.GetInt32(0, available.Count);
            var q = available[index];

            return new SecurityQuestion
            {
                Id = q.Id,
                Question = q.Question,
                Answer = q.Answer,
                ShowPuzzle = q.ShowPuzzle,
                PuzzleHint = q.ShowPuzzle ? GeneratePuzzleHint(q.Answer) : null
            };
        }

        public SecurityQuestion? GetQuestionById(int id)
        {
            var q = _questions.FirstOrDefault(x => x.Id == id);
            if (q == null) return null;

            return new SecurityQuestion
            {
                Id = q.Id,
                Question = q.Question,
                Answer = q.Answer,
                ShowPuzzle = q.ShowPuzzle,
                PuzzleHint = q.ShowPuzzle ? GeneratePuzzleHint(q.Answer) : null
            };
        }

        public bool CheckAnswer(int questionId, string userAnswer)
        {
            if (string.IsNullOrWhiteSpace(userAnswer)) return false;

            var q = _questions.FirstOrDefault(x => x.Id == questionId);
            if (q == null) return false;

            // Remove extra spaces and make case-insensitive
            string cleanAnswer = userAnswer.Trim().Replace("  ", " ").ToLowerInvariant();
            string cleanCorrect = q.Answer.Trim().Replace("  ", " ").ToLowerInvariant();

            // Special logic for Pastor Sydney to accept variations
            if (questionId == 1)
            {
                return cleanAnswer.Contains("fernando") && cleanAnswer.Contains("sydney") && cleanAnswer.Contains("herrera");
            }

            return cleanAnswer == cleanCorrect;
        }

        private string GeneratePuzzleHint(string answer)
        {
            // Example: FERNANDO SYDNEY T. HERRERA -> F_ _ _ _ _ _ O  S_ _ _ _ Y  T.  H_ _ _ _ _ A
            string[] words = answer.Split(' ');
            var hintWords = new List<string>();

            foreach (var word in words)
            {
                if (word.Length <= 2)
                {
                    hintWords.Add(word); // Keep "T." or short words as is
                    continue;
                }

                char first = word[0];
                char last = word[word.Length - 1];
                string middle = new string('_', word.Length - 2);

                // Add spaces between underscores for readability: F _ _ _ O
                middle = string.Join(" ", middle.ToCharArray());

                hintWords.Add($"{first} {middle} {last}");
            }

            return string.Join("   ", hintWords);
        }
    }

    public class SecurityQuestion
    {
        public int Id { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public bool ShowPuzzle { get; set; }
        public string? PuzzleHint { get; set; }
    }
}