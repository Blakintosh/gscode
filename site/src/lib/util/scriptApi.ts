import type { ScrDataType, ScrFunction, ScrFunctionOverload, ScrFunctionParameter } from "$lib/models/library";

export function typeToString(type: ScrDataType | undefined) {
    if(!type || !type.dataType) {
        return "";
    }

    // this could've been avoided with a better design, but alas
    const prefix = type.instanceType ? `${type.dataType} ` : "";
    const suffix = type.isArray ? "[]" : "";

    const dataString = type.instanceType ? type.instanceType : type.dataType;

    return prefix + dataString + suffix;
}

export function overloadToSyntacticString(functionName: string, overload: ScrFunctionOverload) {
    const calledOnType = overload.calledOn ? typeToString(overload.calledOn.type) : "";
    const calledOnSignature = calledOnType ? `${calledOnType} ` : "";

    // e.g. void iprintlnbold
    const returnType = overload.returns ? typeToString(overload.returns.type) : "";
    const signature = returnType ? `: ${returnType}` : "";

    const parameterStrings: string[] = overload.parameters.map((value) => parameterToSyntacticString(value));

    return `${calledOnSignature}${functionName}(${parameterStrings.join(", ")})${signature}`;
}

export function parameterToSyntacticString(parameter: ScrFunctionParameter) {
    const parameterType = typeToString(parameter.type);
    const name = parameter.name ? parameter.name : "unknown";

    if(!parameterType) {
        return name;
    }
    return `${parameterType} ${name}`;
}