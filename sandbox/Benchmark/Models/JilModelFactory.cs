#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Benchmark.Models
{
    //[GenericTypeArguments(typeof(Question))]
    //[GenericTypeArguments(typeof(Answer))]
    //[GenericTypeArguments(typeof(User))]
    //[GenericTypeArguments(typeof(List<Question>))]
    //[GenericTypeArguments(typeof(List<Answer>))]
    //[GenericTypeArguments(typeof(List<User>))]
    //[GenericTypeArguments(typeof(Dictionary<string, Question>))]
    //[GenericTypeArguments(typeof(Dictionary<string, Answer>))]
    //[GenericTypeArguments(typeof(Dictionary<string, User>))]

    internal class JilModelFactory
    {
        Random Rand;

        JilModelFactory()
        {
                
        }

        public static T Create<T>()
        {
            T value = default;
            var factory = new JilModelFactory();

            factory.ResetRand();
            if (!typeof(T).IsGenericType)
            {
                value = (T)factory.MakeSingleObject(typeof(T));
            }
            else
            {
                if (typeof(T).GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    value = (T)factory.MakeDictionaryObject(typeof(T).GetGenericArguments()[1]);
                }
                else if (typeof(T).GetGenericTypeDefinition() == typeof(List<>))
                {
                    value = (T)factory.MakeListObject(typeof(T).GetGenericArguments()[0]);
                }
            }

            return value;
        }

        void ResetRand()
        {
            Rand = new Random(314159265);
        }

        object MakeSingleObject(Type t)
        {
            var ret = Activator.CreateInstance(t);
            foreach (var p in t.GetProperties())
            {
                var propType = p.PropertyType;
                var val = propType.RandomValue(Rand);

                p.SetValue(ret, val);
            }

            return ret;
        }

        object MakeListObject(Type t)
        {
            var asList = typeof(List<>).MakeGenericType(t);

            var ret = asList.RandomValue(Rand);

            // top level can't be null
            if (ret == null)
            {
                return MakeListObject(t);
            }

            return ret;
        }

        object MakeDictionaryObject(Type t)
        {
            var asDictionary = typeof(Dictionary<,>).MakeGenericType(typeof(string), t);
            var ret = Activator.CreateInstance(asDictionary);
            var add = asDictionary.GetMethod("Add");

            var len = Rand.Next(30) + 20;
            for (var i = 0; i < len; i++)
            {
                var key = (string)typeof(string).RandomValue(Rand);
                if (key == null)
                {
                    i--;
                    continue;
                }

                var val = t.RandomValue(Rand);

                add.Invoke(ret, new object[] { key, val });
            }

            return ret;
        }
    }
}
