import fs from 'fs';

// Read the input JSON file
const inputJson = JSON.parse(fs.readFileSync('input.json', 'utf8'));

function toPascalCase(str) {
	if(!str) return;

	return str
	  .replace(/(?:^\w|[A-Z]|\b\w|\s+)/g, (match, index) => {
		if (+match === 0) return '';
		return index === 0 ? match.toUpperCase() : match.toLowerCase();
	  })
	  .replace(/_/g, '');
  }
  
  function toCamelCase(str) {
	if(!str) return;
	
	return str
	  .replace(/(?:^\w|[A-Z]|\b\w|\s+)/g, (match, index) => {
		if (+match === 0) return '';
		return index === 0 ? match.toLowerCase() : match.toUpperCase();
	  })
	  .replace(/_/g, '');
  }

// Process the JSON
inputJson.api.forEach((scrFunction) => {
	// Create the new overload entry
  const newOverload = {
    calledOn: scrFunction.calledOn,
    parameters: scrFunction.parameters,
    returns: scrFunction.returns,
  };

  // Push the new overload entry into the overloads array
  scrFunction.overloads = scrFunction.overloads || [];
  scrFunction.overloads.push(newOverload);


  // Remove calledOn, parameters, and returns from the original positions
  delete scrFunction.calledOn;
  delete scrFunction.parameters;
  delete scrFunction.returns;
});

// Write the output JSON file
fs.writeFileSync('output.json', JSON.stringify(inputJson, null, 2), 'utf8');