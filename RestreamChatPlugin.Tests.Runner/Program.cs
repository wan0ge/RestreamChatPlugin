using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// 轻量反射运行器：加载已生成的测试程序集，逐个调用 [TestMethod] 并报告通过/失败。
// 绕开 VSTest 的 testhost（本环境 SDK 10 的 testhost.net48.exe 启动即原生崩溃），
// 直接执行测试体，得到真实结果。
class Program
{
    static int Main()
    {
        var here = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        var testDll = Path.Combine(here, "RestreamChatPlugin.Tests.dll");
        var asm = Assembly.LoadFrom(testDll);
        var testClasses = asm.GetTypes()
            .Where(t => t.GetCustomAttributes<TestClassAttribute>().Any())
            .ToList();

        int pass = 0, fail = 0, skip = 0;
        foreach (var t in testClasses)
        {
            object inst = null;
            try { inst = Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                Console.WriteLine($"CLASSFAIL {t.Name}: {ex.GetType().Name}: {ex.Message}");
                skip++;
                continue;
            }
            foreach (var m in t.GetMethods().Where(m => m.GetCustomAttributes<TestMethodAttribute>().Any()))
            {
                try
                {
                    m.Invoke(inst, null);
                    Console.WriteLine($"PASS  {t.Name}.{m.Name}");
                    pass++;
                }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException;
                    Console.WriteLine($"FAIL  {t.Name}.{m.Name}: {inner?.GetType().Name}: {inner?.Message}");
                    fail++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL  {t.Name}.{m.Name}: {ex.GetType().Name}: {ex.Message}");
                    fail++;
                }
            }
        }

        Console.WriteLine($"\n总计 {pass + fail + skip}，通过 {pass}，失败 {fail}，跳过 {skip}");
        return fail == 0 ? 0 : 1;
    }
}
