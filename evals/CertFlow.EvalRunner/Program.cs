// CertFlow Agent Evaluation Runner
//
// Runs Coherence, Fluency, and Task Adherence evaluations against three CertFlow agents
// in Microsoft Foundry. Results appear under Build → Evaluations in the Foundry portal.
//
// Required environment variables:
//   CERTFLOW_PROJECT_ENDPOINT   — Foundry project endpoint, e.g.
//                                 https://<account>.services.ai.azure.com/api/projects/<project>
//   CERTFLOW_JUDGE_MODEL        — Judge model deployment name (default: gpt-5-nano)
//
// Run from the repo root:
//   az login
//   dotnet run --project evals/CertFlow.EvalRunner
//
// Portal equivalent (no script needed):
//   1. Build → Data → + New dataset → upload a JSONL file from tests/CertFlow.AgentEvaluationTests/
//   2. Build → Evaluations → + New evaluation
//   3. Step 1 Target: Agent → select the agent
//   4. Step 2 Scope: Individual turns
//   5. Step 3 Data: select the uploaded dataset
//   6. Step 4 Field mapping: auto-detected (query / response / ground_truth)
//   7. Step 5 Configure agents: leave custom prompt empty
//   8. Step 6 Criteria: Coherence + Fluency + Task Adherence; judge model = gpt-5-nano
//   9. Step 7 Review: name e.g. "CertFlow IntentAgent — Quality & Adherence"
//  10. Submit — results ready in ~5 minutes

using System.ClientModel;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;  // brought in transitively by Azure.AI.Projects

var projectEndpoint = Environment.GetEnvironmentVariable("CERTFLOW_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("CERTFLOW_PROJECT_ENDPOINT is not set");
var judgeModel = Environment.GetEnvironmentVariable("CERTFLOW_JUDGE_MODEL") ?? "gpt-5-nano";

var projectClient = new AIProjectClient(new Uri(projectEndpoint), new Azure.Identity.DefaultAzureCredential());
#pragma warning disable OPENAI001
var evalClient = projectClient.ProjectOpenAIClient.GetEvaluationClient();
#pragma warning restore OPENAI001

// Datasets live in tests/CertFlow.AgentEvaluationTests/ relative to the repo root.
// Run this tool from the repo root so Directory.GetCurrentDirectory() resolves correctly.
var datasetDir = Path.Combine(Directory.GetCurrentDirectory(), "tests", "CertFlow.AgentEvaluationTests");

// evalLabel overrides the default "Quality & Adherence" suffix for agents whose dataset
// covers additional dimensions. IntentAgent includes impersonation test cases (rows 11-12)
// that specifically test the sender-identity security boundary.
var agents = new[]
{
    ("ExamOpsIntentAgent",       Path.Combine(datasetDir, "eval-agent1-intent.jsonl"),       "Quality, Adherence & Security"),
    ("ExamOpsPolicyAgent",       Path.Combine(datasetDir, "eval-agent2-policy.jsonl"),       "Quality & Adherence"),
    ("ExamOpsConfirmationAgent", Path.Combine(datasetDir, "eval-agent3-confirmation.jsonl"), "Quality & Adherence"),
};

// Evaluators chosen for certification exam rescheduling:
//   Coherence     — responses must not contradict exam policy (e.g., say rescheduling is free
//                   and then imply a charge, or propose a slot outside the candidate's city)
//   Fluency       — ClarificationNeeded and FailReason strings surface in candidate emails;
//                   poor phrasing in these reflects on the certification body
//   Task Adherence — each agent must stay in its role: Intent Agent must not act on
//                   third-party emails in the body; Policy Agent must respect the 48h cutoff;
//                   Confirmation Agent must not commit without explicit candidate approval
object[] evaluators =
[
    new
    {
        type = "azure_ai_evaluator",
        name = "coherence",
        evaluator_name = "builtin.coherence",
        initialization_parameters = new { deployment_name = judgeModel },
        data_mapping = new { query = "{{item.query}}", response = "{{sample.output_text}}" }
    },
    new
    {
        type = "azure_ai_evaluator",
        name = "fluency",
        evaluator_name = "builtin.fluency",
        initialization_parameters = new { deployment_name = judgeModel },
        data_mapping = new { query = "{{item.query}}", response = "{{sample.output_text}}" }
    },
    new
    {
        type = "azure_ai_evaluator",
        name = "task_adherence",
        evaluator_name = "builtin.task_adherence",
        initialization_parameters = new { deployment_name = judgeModel },
        // output_items carries the full tool-call trace, which Task Adherence needs to assess
        // whether the agent called the right tools in the right sequence for each role.
        data_mapping = new { query = "{{item.query}}", response = "{{sample.output_items}}" }
    },
];

bool anyFailed = false;

foreach (var (agentName, datasetPath, evalLabel) in agents)
{
    Console.WriteLine($"\n=== {agentName} ===");

    if (!File.Exists(datasetPath))
    {
        Console.WriteLine($"  Dataset not found: {datasetPath}");
        anyFailed = true;
        continue;
    }

    // Load JSONL rows for inline dataset (avoids file-upload API; works for ≤50 rows)
    var rows = File.ReadLines(datasetPath)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => (object)new { item = JsonSerializer.Deserialize<JsonElement>(l) })
        .ToArray();

    Console.WriteLine($"  Loaded {rows.Length} rows from {Path.GetFileName(datasetPath)}");

    // Step 1 — Create evaluation definition (evaluators + data schema)
    var evalPayload = BinaryData.FromObjectAsJson(new
    {
        name = $"{agentName} — {evalLabel}",
        data_source_config = new
        {
            type = "custom",
            item_schema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    ground_truth = new { type = "string" }
                },
                required = new[] { "query" }
            },
            include_sample_schema = true
        },
        testing_criteria = evaluators
    });

    ClientResult evalResult = await evalClient.CreateEvaluationAsync(BinaryContent.Create(evalPayload));
    var evalJson = JsonSerializer.Deserialize<JsonElement>(evalResult.GetRawResponse().Content.ToString());
    var evalId = evalJson.GetProperty("id").GetString()!;
    Console.WriteLine($"  Evaluation created: {evalId}");

    // Step 2 — Create a run that invokes the live Foundry agent against the inline dataset
    var runPayload = BinaryData.FromObjectAsJson(new
    {
        name = $"{agentName} — Run {DateTimeOffset.UtcNow:yyyyMMdd-HHmm}",
        data_source = new
        {
            type = "azure_ai_target_completions",
            source = new
            {
                type = "file_content",
                content = rows
            },
            input_messages = new
            {
                type = "template",
                template = new[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new { type = "input_text", text = "{{item.query}}" }
                    }
                }
            },
            target = new
            {
                type = "azure_ai_agent",
                name = agentName
                // version omitted → Foundry uses the latest registered version
            }
        }
    });

    ClientResult runResult = await evalClient.CreateEvaluationRunAsync(evalId, BinaryContent.Create(runPayload));
    var runJson = JsonSerializer.Deserialize<JsonElement>(runResult.GetRawResponse().Content.ToString());
    var runId = runJson.GetProperty("id").GetString()!;
    Console.WriteLine($"  Run created: {runId}");

    // Step 3 — Poll until complete
    string status;
    do
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        ClientResult poll = await evalClient.GetEvaluationRunAsync(evalId, runId, null);
        var pollJson = JsonSerializer.Deserialize<JsonElement>(poll.GetRawResponse().Content.ToString());
        status = pollJson.GetProperty("status").GetString()!;
        Console.Write($"\r  Status: {status,-20}");
    } while (status is not ("completed" or "failed" or "canceled"));

    Console.WriteLine();

    // Step 4 — Print results URL
    ClientResult final = await evalClient.GetEvaluationRunAsync(evalId, runId, null);
    var finalJson = JsonSerializer.Deserialize<JsonElement>(final.GetRawResponse().Content.ToString());

    if (status == "completed")
    {
        var url = finalJson.TryGetProperty("report_url", out var u) ? u.GetString() : "(no URL)";
        Console.WriteLine($"  Results: {url}");
    }
    else
    {
        var errMsg = finalJson.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
            ? m.GetString() : "(no details)";
        Console.WriteLine($"  Run {status}: {errMsg}");
        Console.WriteLine($"  (eval: {evalId}, run: {runId})");
        anyFailed = true;
    }
}

Console.WriteLine(anyFailed ? "\nSome runs failed." : "\nAll evaluation runs completed.");
return anyFailed ? 1 : 0;
