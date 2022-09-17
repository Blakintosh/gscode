

#namespace example;

function main()
{
	i = 0;

	while(i < 10)
	{
		iprintlnbold("yes!");
		if(RandomIntRange(2, 5) == 3)
		{
			i++;
		}
		else
		{
			i--;
		}
	}
}