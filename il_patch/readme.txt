- Create directory 'il'
- Decompile using VS developer command prompt:
	ildasm /out=il\il.txt input\Microsoft.VisualStudio.Services.Content.Common.dll
- Apply patch (eg with msysgit) and fix line endings
	cd il
	patch < ../freebsd.patch
	unix2dos il.txt
- Recompile:
	ilasm il.txt /res:il.res /dll /output:..\output\Microsoft.VisualStudio.Services.Content.Common.dll
