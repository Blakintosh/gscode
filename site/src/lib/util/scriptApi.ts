import type { ScrDataType, ScrFunction, ScrFunctionOverload, ScrFunctionParameter } from "$lib/models/library";

export function singleTypeToString(type: ScrDataType | undefined): string {
    if (!type || !type.dataType) {
        return "";
    }

    const subType = type.subType ?? type.instanceType;

    // For entity types, show the entity subtype (e.g., "player", "weapon")
    // If no subtype or subtype is empty, just show "entity"
    if (type.dataType === "entity") {
        if (subType) {
            return subType;
        }
        return "entity";
    }

    // For enum types, show the enum name if available
    if (type.dataType === "enum" && subType) {
        return subType;
    }

    // For struct and other types, just show the dataType
    return type.dataType;
}

export function typeToString(type: ScrDataType | undefined): string {
    if (!type || !type.dataType) {
        return "";
    }

    const suffix = type.isArray ? "[]" : "";

    // Handle union types
    if (type.unionOf && type.unionOf.length > 0) {
        const unionParts = type.unionOf.map(t => singleTypeToString(t));
        return unionParts.join(" | ") + suffix;
    }

    return singleTypeToString(type) + suffix;
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
    if(parameter.type?.dataType === "vararg") {
        return "...";
    }

    const parameterType = typeToString(parameter.type);
    const name = parameter.name ? parameter.name : "unknown";
    const prefix = parameter.variadic ? "..." : "";

    if (!parameterType) {
        return prefix + name;
    }
    return `${parameterType} ${prefix}${name}`;
}