using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;
using Newtonsoft.Json;

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		try
		{
			Console.WriteLine("Starting minimal Workflows repro: building single FunctionExecutor that yields output.");

			// handler that logs, yields, and logs again
			var handler = new Func<object, IWorkflowContext, CancellationToken, ValueTask>(async (inp, ctx, ct) =>
			{
				try { Console.WriteLine("REPRO_HANDLER_START"); } catch { }
				await ctx.YieldOutputAsync(new { hello = "world", ts = DateTime.UtcNow });
				try { Console.WriteLine("REPRO_HANDLER_AFTER_YIELD"); } catch { }
			});

			var execOptions = ExecutorOptions.Default;
			var exec = new FunctionExecutor<object>("Repro", handler, execOptions);
			var ish = new ExecutorIsh(exec);
			var builder = new WorkflowBuilder(ish);

			// make the single executor the workflow output
			builder.WithOutputFrom(new[] { ish });
			var wf = builder.Build();

			// Inspect workflow internals
			try
			{
				Console.WriteLine($"Workflow type: {wf.GetType().FullName}");
				var regsField = wf.GetType().GetField("<Registrations>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
				if (regsField != null)
				{
					var regsVal = regsField.GetValue(wf);
					if (regsVal is System.Collections.IEnumerable regsEnum)
					{
						int cnt = 0;
						foreach (var kv in regsEnum) { cnt++; }
						Console.WriteLine($"Registrations count approx: {cnt}");
					}
				}
			}
			catch (Exception ex) { Console.WriteLine("Workflow inspect failed: " + ex.Message); }

			var runId = Guid.NewGuid().ToString();
			Console.WriteLine($"Running workflow repro runId={runId} (no-explicit-edge)");
			var runResult = await InProcessExecution.RunAsync(wf, new { }, runId, CancellationToken.None);

			Console.WriteLine("Run completed. Inspecting run result:");
			try
			{
				var runJson = JsonConvert.SerializeObject(runResult, Formatting.Indented);
				Console.WriteLine(runJson);
			}
			catch (Exception ex)
			{
				Console.WriteLine("Failed to serialize run result: " + ex);
			}

			// Second experiment: create a builder and explicitly add a self-edge to the single executor
			try
			{
				Console.WriteLine("\n--- Running second experiment: explicit self-edge before Build");
				var builder2 = new WorkflowBuilder(ish);
				builder2.AddEdge(ish, ish);
				builder2.WithOutputFrom(new[] { ish });
				var wf2 = builder2.Build();

				try
				{
					var regsField2 = wf2.GetType().GetField("<Registrations>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
					if (regsField2 != null)
					{
						var regsVal2 = regsField2.GetValue(wf2);
						if (regsVal2 is System.Collections.IEnumerable regsEnum2)
						{
							int cnt2 = 0;
							foreach (var kv in regsEnum2) { cnt2++; }
							Console.WriteLine($"WF2 Registrations count approx: {cnt2}");
						}
					}
				}
				catch { }

				var runId2 = Guid.NewGuid().ToString();
				Console.WriteLine($"Running workflow repro runId={runId2} (with-self-edge)");
				var runResult2 = await InProcessExecution.RunAsync(wf2, new { }, runId2, CancellationToken.None);
				Console.WriteLine("Run2 completed. Inspecting run result:");
				try
				{
					var runJson2 = JsonConvert.SerializeObject(runResult2, Formatting.Indented);
					Console.WriteLine(runJson2);
				}
				catch (Exception ex)
				{
					Console.WriteLine("Failed to serialize run2 result: " + ex);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Second experiment failed: " + ex);
			}

			return 0;
		}
		catch (Exception ex)
		{
			Console.WriteLine("Repro failed: " + ex);
			return 2;
		}
	}
}
