using NoxMarsanAssistant;

var accounts = new[]
{
    "COMPRA TORAS AZA",
    "VENDA SANTA CLARA",
    "VENDA SANTA CLARA NFE",
    "COMPRA MAD. TIOTO",
    "COMPRA TORAS GUSE",
    "COMPRA TORAS VALNEI",
    "VENDA SERRAGEM GELENSKI",
    "VENDA MAD. ADENIR"
};

var settings = new VoiceRecognitionSettings();
var training = new VoiceTrainingProfile();
training.Terms["TORAS AZA"] = ["horas asia", "toras asia", "tora asa", "toras asa"];
training.Terms["TORAS GUSE"] = ["guze"];
training.Terms["SERRAGEM GELENSKI"] = ["gelensqui"];
training.Terms["MAD. TIOTO"] = ["tioto"];
var entities = MarsanVocabulary.BuildEntities(accounts, training);
var interpreter = new MarsanVoiceInterpreter();
var failures = new List<string>();

void Expect(string input, MarsanIntent expectedIntent, string? expectedEntityContains, bool shouldExecute, bool? confirm = null)
{
    var result = interpreter.Interpret(input, entities, settings, false);
    var entityOk = expectedEntityContains is null ||
        (result.Entity?.DisplayName.Contains(expectedEntityContains, StringComparison.OrdinalIgnoreCase) ?? false);
    var ok = result.Intent == expectedIntent &&
             entityOk &&
             result.ShouldExecute == shouldExecute &&
             (confirm is null || result.RequiresConfirmation == confirm.Value);

    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | {input}");
    Console.WriteLine($"  {result.Intent} {result.IntentScore:P0} | {result.Entity?.DisplayName ?? "-"} {result.EntityScore?.FinalScore:P0} | Execute={result.ShouldExecute} Confirm={result.RequiresConfirmation}");
    if (!ok)
    {
        failures.Add(input);
        foreach (var line in result.Diagnostics) Console.WriteLine("  " + line);
    }
}

Expect("prima horas ásia", MarsanIntent.Print, "TORAS AZA", true);
Expect("marsam imprime santa clara", MarsanIntent.Print, "SANTA CLARA", true);
Expect("Marsan imprima Toras Aza", MarsanIntent.Print, "TORAS AZA", true);
Expect("imprime pra mim Santa Clara", MarsanIntent.Print, "SANTA CLARA", true);
Expect("pode imprimir a Santa Clara", MarsanIntent.Print, "SANTA CLARA", true);
Expect("quero a planilha da Santa Clara impressa", MarsanIntent.Print, "SANTA CLARA", true);
Expect("imprimir planilha Tioto", MarsanIntent.Print, "TIOTO", true);
Expect("prima tora za", MarsanIntent.Print, "TORAS AZA", false, true);
Expect("imprima horas aza", MarsanIntent.Print, "TORAS AZA", true);
Expect("imprima toras asa", MarsanIntent.Print, "TORAS AZA", true);
Expect("imprima guze", MarsanIntent.Print, "GUSE", true);
Expect("imprima gelensqui", MarsanIntent.Print, "GELENSKI", true);
Expect("abra Santa Clara", MarsanIntent.Open, "SANTA CLARA", true);

Expect("imprima santa", MarsanIntent.Print, "SANTA CLARA", false, true);
Expect("imprima documento inexistente", MarsanIntent.Print, null, false);
Expect("imprima", MarsanIntent.Print, null, false);
Expect("quero falar com o financeiro", MarsanIntent.Open, null, false);

foreach (var w in new[] { "marsan", "marsam", "marçam", "marzan", "maçan" })
{
    var score = MarsanVocabulary.WakeScore(w);
    var ok = score >= settings.WakeExecuteThreshold;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | wake {w} = {score:P0}");
    if (!ok) failures.Add("wake:" + w);
}

foreach (var w in new[] { "maçã", "mar", "casa", "marcio", "bom dia pessoal", "santa clara" })
{
    var score = MarsanVocabulary.WakeScore(w);
    var ok = score < settings.WakeExecuteThreshold;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} | no-wake {w} = {score:P0}");
    if (!ok) failures.Add("false-wake:" + w);
}

if (failures.Count > 0)
    throw new InvalidOperationException("Falharam: " + string.Join(", ", failures));

Console.WriteLine("Todos os testes de interpretação de voz passaram.");
