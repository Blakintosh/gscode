/* */
/*
"Summary: Adds a function to be called when a scene starts"
"SPMP: shared"


"Name: add_scene_func( str_scenedef, func, str_state = "play" )"
"CallOn: level"
"MandatoryArg: <str_scenedef> Name of scene"
"MandatoryArg: <func> function to call when scene starts"
"OptionalArg: [str_state] set to "init" or "done" if you want to the function to get called in one of those states"	
"Example: level scene::init( "my_scenes", "targetname" );"
*/
function test()
{
	IPrintLnBold("it works!");
}