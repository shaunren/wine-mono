using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

class RunTests
{
	string ExePath;
	string BasePath;

	string passing_output_path;
	string failing_output_path;

	// expected results
	//HashSet<string> pass_list;
	//HashSet<string> todo_list;
	Dictionary<string, List<string>> skip_list = new Dictionary<string, List<string>> ();

	// actual results
	List<string> passing_tests = new List<string> ();
	List<string> failing_tests = new List<string> ();

	bool test_timed_out;

	RunTests ()
	{
		ExePath = Environment.GetCommandLineArgs()[0];
		BasePath = Path.GetDirectoryName(ExePath);
	}

	void process_mono_test_output(object o)
	{
		var t = (Tuple<Process,string>)o;
		Process p = t.Item1;
		string test_assembly = t.Item2;
		string line;
		string current_test = null;
		bool any_passed = false;
		bool any_failed = false;

		while ((line = p.StandardOutput.ReadLine ()) != null)
		{
			Console.WriteLine(line);
			if (line.StartsWith("Running '") && line.EndsWith("' ..."))
			{
				if (current_test != null)
				{
					passing_tests.Add(String.Format("{0}:{1}", test_assembly, current_test));
					any_passed = true;
				}
				current_test = line.Substring(9, line.Length - 14);
			}
			else if (line.StartsWith(String.Format("{0} failed: got ", current_test)))
			{
				failing_tests.Add(String.Format("{0}:{1}", test_assembly, current_test));
				current_test = null;
				any_failed = true;
			}
			else if (line.StartsWith("Regression tests: ") &&
				line.Contains(" ran, ") && line.Contains(" failed in "))
			{
				if (current_test != null)
				{
					passing_tests.Add(String.Format("{0}:{1}", test_assembly, current_test));
					any_passed = true;
					current_test = null;
				}
			}
		}

		if (current_test != null)
		{
			p.WaitForExit();
			failing_tests.Add(String.Format("{0}:{1}", test_assembly, current_test));
		}

		if (test_timed_out)
		{
			failing_tests.Add(test_assembly);
			Console.WriteLine("Test timed out: {0}", test_assembly);
		}
		else if (p.ExitCode == 0 && !any_failed)
		{
			passing_tests.Add(test_assembly);
			Console.WriteLine("Test succeeded: {0}", test_assembly);
		}
		else if (any_passed)
		{
			Console.WriteLine("Some tests succeeded: {0}", test_assembly);
		}
		else
		{
			failing_tests.Add(test_assembly);
			Console.WriteLine("Test failed{0}: {1}", p.ExitCode, test_assembly);
		}
	}

	void run_mono_test_exe(string path, string arch)
	{
		string testname = Path.GetFileNameWithoutExtension(path);
		string fulltestname = String.Format("{0}.{1}", arch, testname);
		test_timed_out = false;

		List<string> skips = new List<string> ();
		if (skip_list.ContainsKey(testname))
		{
			if (skip_list[testname] == null)
			{
				Console.WriteLine("Skipping {0}", fulltestname);
				return;
			}
			skips.AddRange(skip_list[testname]);
		}
		if (skip_list.ContainsKey(fulltestname))
		{
			if (skip_list[fulltestname] == null)
			{
				Console.WriteLine("Skipping {0}", fulltestname);
				return;
			}
			skips.AddRange(skip_list[fulltestname]);
		}

		Console.WriteLine("Starting test: {0}", fulltestname);
		Process p = new Process ();
		p.StartInfo = new ProcessStartInfo (path);
		p.StartInfo.Arguments = "--verbose";
		foreach (string test in skips)
		{
			p.StartInfo.Arguments = String.Format("{0} --exclude-test \"{1}\"",
				p.StartInfo.Arguments, test);
		}
		p.StartInfo.UseShellExecute = false;
		p.StartInfo.RedirectStandardOutput = true;
		p.StartInfo.WorkingDirectory = Path.GetDirectoryName(path);
		p.Start();
		Thread t = new Thread(process_mono_test_output);
		t.Start(Tuple.Create(p, fulltestname));
		p.WaitForExit(5 * 60 * 1000); // 5 minutes
		if (!p.HasExited)
		{
			test_timed_out = true;
			p.Kill();
		}
		t.Join(10 * 1000);
		if (t.IsAlive)
		{
			p.StandardOutput.Close();
			t.Join();
		}
	}

	void run_mono_test_dir(string path, string arch)
	{
		foreach (string filename in Directory.EnumerateFiles(path, "*.exe"))
		{
			run_mono_test_exe(filename, arch);
		}
	}

	void add_skip(string skip)
	{
		if (skip.Contains(":"))
		{
			int index = skip.IndexOf(':');
			string assembly = skip.Substring(0, index);
			string test = skip.Substring(index+1);
			List <string> l;
			if (!skip_list.TryGetValue(assembly, out l) || l == null)
			{
				l = skip_list[assembly] = new List<string>();
			}
			l.Add(test);
		}
		else
		{
			skip_list.TryAdd(skip, null);
		}
	}

	void read_skiplist(string filename)
	{
		using (StreamReader sr = new StreamReader(filename))
		{
			string line;
			while ((line = sr.ReadLine()) != null)
			{
				if (line.Contains("#"))
				{
					line = line.Substring(0, line.IndexOf('#'));
				}
				foreach (string test in line.Split(new char[] {' '}))
				{
					string trtest = test.Trim();
					if (trtest != "")
						add_skip(trtest);
				}
			}
		}
	}

	int main(string[] arguments)
	{
		foreach (string argument in arguments)
		{
			if (argument.StartsWith("-write-passing:"))
				passing_output_path = argument.Substring(15);
			else if (argument.StartsWith("-write-failing:"))
				failing_output_path = argument.Substring(15);
			else if (argument.StartsWith("-skip:"))
				add_skip(argument.Substring(6));
			else if (argument.StartsWith("-skip-list:"))
				read_skiplist(argument.Substring(11));
			else
			{
				Console.WriteLine("Unrecognized argument: {0}", argument);
				return 1;
			}
		}

		run_mono_test_dir(Path.Combine(BasePath, "tests-x86"), "x86");
		run_mono_test_dir(Path.Combine(BasePath, "tests-x86_64"), "x86_64");

		if (!String.IsNullOrEmpty(passing_output_path))
		{
			using (var f = new StreamWriter(passing_output_path))
			{
				foreach (string name in passing_tests)
					f.WriteLine(name);
			}
		}

		if (!String.IsNullOrEmpty(failing_output_path))
		{
			using (var f = new StreamWriter(failing_output_path))
			{
				foreach (string name in failing_tests)
					f.WriteLine(name);
			}
		}

		Console.WriteLine("{0} tests passed, {1} tests failed",
			passing_tests.Count, failing_tests.Count);

		return failing_tests.Count;
	}

	static int Main(string[] arguments)
	{
		RunTests instance = new RunTests();
		return instance.main(arguments);
	}
}
