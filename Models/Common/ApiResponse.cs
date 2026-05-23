namespace AISupportAnalysisPlatform.Models.Common
{
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }

        public static ApiResponse Ok(string? message = null) => new ApiResponse { Success = true, Message = message };
        public static ApiResponse Fail(string? error = null) => new ApiResponse { Success = false, Error = error };
    }

    public class ApiResponse<T> : ApiResponse
    {
        public T? Data { get; set; }

        public static ApiResponse<T> Ok(T data, string? message = null) => new ApiResponse<T> { Success = true, Data = data, Message = message };
        public static new ApiResponse<T> Fail(string? error = null) => new ApiResponse<T> { Success = false, Error = error };
    }
}
